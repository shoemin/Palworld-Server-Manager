using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

// Trusted Host/offline persistence seam. Caller owns the machine lease; no private material.
public sealed class HostCredentialStateRepository(HostDatabase database, Guid hostId)
{
    public const string TlsPurpose = "HostTlsV1";
    private readonly HostDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly Guid _hostId = hostId != Guid.Empty ? hostId : throw new ArgumentException("Host identity required.");
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=_database.DatabasePath, Mode=SqliteOpenMode.ReadWrite, Pooling=false, ForeignKeys=true }.ToString());
        try { c.Open(); return c; } catch { c.Dispose(); throw; }
    }
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] args)
    {
        var command=c.CreateCommand(); command.Transaction=tx; command.CommandText=sql;
        foreach (var (name,value) in args) command.Parameters.AddWithValue(name,value??DBNull.Value); return command;
    }
    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] args)
    { using var command=Command(c,tx,sql,args); command.ExecuteNonQuery(); }
    private static string Reference(string value) => value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
        ? value : throw new ArgumentException("Invalid opaque credential reference.");
    private (bool Initialized, string? Current) Identity(SqliteConnection c, SqliteTransaction tx)
    {
        using var command=Command(c,tx,"SELECT HostId,HostBootstrapState,CurrentCredentialRef FROM HostIdentity WHERE Id=1;");
        using var r=command.ExecuteReader();
        if (!r.Read() || r.GetString(0)!=_hostId.ToString("D")) throw new InvalidDataException("Authoritative Host identity mismatch.");
        var initialized=r.GetString(1) switch { "Initialized"=>true, "Uninitialized"=>false, _=>throw new InvalidDataException("Unknown bootstrap state.") };
        var current=r.IsDBNull(2)?null:r.GetString(2); r.Close();
        if (HostIdentityRepository.CountActiveOwners(c,tx)!=(initialized?1:0)) throw new InvalidDataException("Invalid Owner cardinality.");
        return (initialized,current);
    }
    public HostCredentialSnapshot Read()
    {
        using var c=Open(); using var tx=c.BeginTransaction(deferred:true); return Read(c,tx);
    }
    private HostCredentialSnapshot Read(SqliteConnection c, SqliteTransaction tx)
    {
        var identity=Identity(c,tx); var credentials=new List<HostCredentialMetadata>(); var rotations=new List<HostRotationMetadata>();
        using (var command=Command(c,tx,"SELECT CredentialRef,PublicKeyFingerprint,RetiredUtc FROM SecureCredentialReferences WHERE Purpose=$purpose;",("$purpose",TlsPurpose)))
        using (var r=command.ExecuteReader()) while(r.Read()) credentials.Add(new(Reference(r.GetString(0)),r.IsDBNull(1)?null:r.GetString(1),!r.IsDBNull(2)));
        using (var command=Command(c,tx,"SELECT RotationId,OldCredentialRef,NewCredentialRef,State FROM HostCredentialRotations;"))
        using (var r=command.ExecuteReader()) while(r.Read())
        {
            if (!Guid.TryParseExact(r.GetString(0),"D",out var id) || id==Guid.Empty || !Enum.TryParse<HostCredentialRotationState>(r.GetString(3),out var state) || !Enum.IsDefined(state))
                throw new InvalidDataException("Invalid persisted rotation state.");
            rotations.Add(new(id,r.IsDBNull(1)?null:Reference(r.GetString(1)),r.IsDBNull(2)?null:Reference(r.GetString(2)),state));
        }
        return new(_hostId,identity.Initialized,identity.Current,credentials.AsReadOnly(),rotations.AsReadOnly());
    }
    public void PlanCredential(string reference)
    {
        using var c=Open(); using var tx=c.BeginTransaction(deferred:false); Identity(c,tx);
        Execute(c,tx,"INSERT INTO SecureCredentialReferences (CredentialRef,Purpose,CreatedUtc) VALUES ($ref,$purpose,$now);",
            ("$ref",Reference(reference)),("$purpose",TlsPurpose),("$now",DateTimeOffset.UtcNow.ToString("O"))); tx.Commit();
    }
    public void RecordCreated(string reference, string publicFingerprint)
    {
        if (!HostTrustPlanning.Fingerprint(publicFingerprint)) throw new ArgumentException("Invalid public key fingerprint.");
        using var c=Open(); using var tx=c.BeginTransaction(deferred:false); Identity(c,tx);
        using var command=Command(c,tx,"""
            UPDATE SecureCredentialReferences SET PublicKeyFingerprint=$fp WHERE CredentialRef=$ref AND Purpose=$purpose
                AND RetiredUtc IS NULL AND (PublicKeyFingerprint IS NULL OR PublicKeyFingerprint=$fp);
            """,("$ref",Reference(reference)),("$purpose",TlsPurpose),("$fp",publicFingerprint));
        if(command.ExecuteNonQuery()!=1) throw new InvalidDataException("Credential was not planned or its metadata changed."); tx.Commit();
    }
    private string Ready(SqliteConnection c, SqliteTransaction tx, string reference)
    {
        using var command=Command(c,tx,"SELECT PublicKeyFingerprint FROM SecureCredentialReferences WHERE CredentialRef=$ref AND Purpose=$purpose AND RetiredUtc IS NULL AND ActivatedUtc IS NULL;",
            ("$ref",Reference(reference)),("$purpose",TlsPurpose));
        var fingerprint=command.ExecuteScalar() as string;
        if(!HostTrustPlanning.Fingerprint(fingerprint)) throw new InvalidDataException("Credential public metadata is not ready.");
        return fingerprint!;
    }
    public void InstallInitial(string reference)
    {
        using var c=Open(); using var tx=c.BeginTransaction(deferred:false); var identity=Identity(c,tx);
        if(identity.Initialized || identity.Current is not null) throw new InvalidOperationException("Initial credential installation is no longer available.");
        Ready(c,tx,reference); Execute(c,tx,"UPDATE HostIdentity SET CurrentCredentialRef=$ref WHERE Id=1;",("$ref",reference));
        Execute(c,tx,"UPDATE SecureCredentialReferences SET ActivatedUtc=$now WHERE CredentialRef=$ref;",("$ref",reference),("$now",DateTimeOffset.UtcNow.ToString("O")));
        Audit(c,tx,"HostMachineCredentialInitialized"); tx.Commit();
    }
    // Actual offline privilege/exclusivity must be enforced by the executable caller (42d2c).
    // No old private credential is read; only ordinary authoritative metadata participates.
    public void ReplaceOffline(string reference, MachineCredentialRecoveryReason reason)
    {
        if(!Enum.IsDefined(reason)) throw new ArgumentException("Unknown recovery reason.");
        using var c=Open(); using var tx=c.BeginTransaction(deferred:false); var identity=Identity(c,tx);
        var fingerprint=Ready(c,tx,reference);
        if(identity.Current is null || identity.Current==reference) throw new InvalidOperationException("Recovery requires a different existing identity credential.");
        // The replacement must be unused, never a historical current/staged credential.
        using(var used=Command(c,tx,"SELECT COUNT(*) FROM HostCredentialRotations WHERE OldCredentialRef=$ref OR NewCredentialRef=$ref;",("$ref",reference)))
            if(Convert.ToInt32(used.ExecuteScalar())!=0) throw new InvalidOperationException("Recovery credential must be fresh.");
        using(var reused=Command(c,tx,"""
            SELECT COUNT(*) FROM SecureCredentialReferences WHERE Purpose=$purpose AND CredentialRef<>$ref AND PublicKeyFingerprint=$fp
                AND (ActivatedUtc IS NOT NULL OR CredentialRef IN (SELECT OldCredentialRef FROM HostCredentialRotations UNION SELECT NewCredentialRef FROM HostCredentialRotations));
            """,("$purpose",TlsPurpose),("$ref",reference),("$fp",fingerprint)))
            if(Convert.ToInt32(reused.ExecuteScalar())!=0) throw new InvalidOperationException("Recovery cannot reuse a prior credential under a new reference.");
        Execute(c,tx,"""
            UPDATE HostIdentity SET CurrentCredentialRef=$ref WHERE Id=1;
            UPDATE SecureCredentialReferences SET ActivatedUtc=$now WHERE CredentialRef=$ref;
            UPDATE HostCredentialRotations SET State='Aborted',CompletedUtc=$now WHERE State IN ('Prepared','Staging','ReadyForCutover','CutOver');
            UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE State='Active';
            """,("$ref",reference),("$now",DateTimeOffset.UtcNow.ToString("O")));
        Audit(c,tx,reason==MachineCredentialRecoveryReason.CredentialLoss?"HostCredentialRecoveredFromLoss":"HostCredentialRecoveredFromCompromise"); tx.Commit();
    }
    public void RecordRetired(string reference)
    {
        using var c=Open(); using var tx=c.BeginTransaction(deferred:false); var plan=HostTrustPlanning.Build(Read(c,tx));
        if(plan.Retained.Contains(reference,StringComparer.Ordinal)) throw new InvalidOperationException("Cannot retire an authoritative retained credential.");
        Execute(c,tx,"UPDATE SecureCredentialReferences SET RetiredUtc=COALESCE(RetiredUtc,$now) WHERE CredentialRef=$ref AND Purpose=$purpose;",
            ("$ref",Reference(reference)),("$purpose",TlsPurpose),("$now",DateTimeOffset.UtcNow.ToString("O"))); tx.Commit();
    }
    public bool HasEnrollmentHistory()
    {
        using var c=Open(); using var tx=c.BeginTransaction(deferred:true); Identity(c,tx);
        using var command=Command(c,tx,"""
            SELECT EXISTS(SELECT 1 FROM PendingOwnerEnrollments) OR EXISTS(SELECT 1 FROM PendingLocalPrincipalEnrollments)
                OR EXISTS(SELECT 1 FROM PendingOwnerCredentialRotations) OR EXISTS(SELECT 1 FROM PendingOwnerRehomes);
            """); return Convert.ToInt32(command.ExecuteScalar())!=0;
    }
    private void Audit(SqliteConnection c, SqliteTransaction tx, string kind) => Execute(c,tx,"""
        INSERT INTO AuditEvents (AuditEventId,OccurredUtc,EventKind,ActorKind,AffectedHostId,IsOfflineRecovery,Summary)
        VALUES ($id,$now,$kind,'OfflineRecovery',$host,1,'Machine identity credential metadata changed.');
        """,("$id",Guid.NewGuid().ToString("D")),("$now",DateTimeOffset.UtcNow.ToString("O")),("$kind",kind),("$host",_hostId.ToString("D")));
}
