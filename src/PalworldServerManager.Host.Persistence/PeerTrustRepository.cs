using System.Globalization;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public enum PeerBindingDisposition { PeerBoundCreated, ResumePeerBound, ActiveReconfirmed, ReplacementRequired, RecoveryRequired }
public sealed record PeerBindingResult(PeerBindingDisposition Disposition, Guid PeerHostId, DateTimeOffset? ExpiresUtc, Guid? ReplacementId = null);
public sealed record PeerTrustRecord(Guid PeerHostId, string State, string? CurrentFingerprint,
    bool RecoveryRequired, string? LocalBoundFingerprint, DateTimeOffset? ExpiresUtc,
    string? PendingFingerprint = null, bool PendingReconfirmationRequired = false);

// Trusted online Host persistence primitive; the caller retains the machine lease. Public
// fingerprints here must come from verified PAKE/mTLS, never unchecked request fields.
// No private material or cross-Host transaction. Activation is in the sibling partial;
// canonical grant issuance remains the required #45 hook's responsibility.
public sealed partial class PeerTrustRepository(HostDatabase database, Guid hostId, TimeProvider? timeProvider = null)
{
    private readonly HostDatabase database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly Guid hostId = hostId != Guid.Empty ? hostId : throw new ArgumentException("Host identity required.");
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(30);
    private static string Id(Guid id) => id != Guid.Empty ? id.ToString("D") : throw new ArgumentException("Identity required.");
    private static string Fingerprint(string value) => HostTrustPlanning.Fingerprint(value) ? value : throw new ArgumentException("Invalid public fingerprint.");
    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Date(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    private SqliteConnection Open(int timeoutSeconds = 30)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = database.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = true, DefaultTimeout = timeoutSeconds
        }.ToString());
        try { c.Open(); return c; } catch { c.Dispose(); throw; }
    }
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] args)
    {
        var command = c.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] args)
    { using var command = Command(c, tx, sql, args); command.ExecuteNonQuery(); }
    private string RequireHost(SqliteConnection c, SqliteTransaction tx)
    {
        using var command = Command(c, tx, """
            SELECT h.HostId,h.HostBootstrapState,s.PublicKeyFingerprint,s.RetiredUtc,s.Purpose
            FROM HostIdentity h LEFT JOIN SecureCredentialReferences s ON s.CredentialRef=h.CurrentCredentialRef WHERE h.Id=1;
            """);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != Id(hostId) || reader.GetString(1) != "Initialized" ||
            reader.IsDBNull(2) || !reader.IsDBNull(3) || reader.GetString(4) != HostCredentialStateRepository.TlsPurpose)
            throw new InvalidDataException("Authoritative Host credential is unavailable.");
        var fingerprint = Fingerprint(reader.GetString(2)); reader.Close();
        if (HostIdentityRepository.CountActiveOwners(c, tx) != 1) throw new InvalidDataException("Invalid Owner cardinality.");
        return fingerprint;
    }
    private static PeerTrustRecord? Read(SqliteConnection c, SqliteTransaction tx, Guid peer)
    {
        using var command = Command(c, tx, """
            SELECT t.State,t.CurrentTrustedPublicKeyFingerprint,t.PeerRecoveryRequired,p.LocalBoundPublicKeyFingerprint,p.ExpiresUtc,
                t.PendingTrustedPublicKeyFingerprint,t.PendingReconfirmationRequired
            FROM TrustedManagers t LEFT JOIN TrustedManagerPairings p ON p.PeerHostId=t.PeerHostId WHERE t.PeerHostId=$peer;
            """, ("$peer", Id(peer)));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        var state = reader.GetString(0);
        if (state is not ("PeerBound" or "Active" or "Revoked")) throw new InvalidDataException("Unknown peer trust state.");
        return new(peer, state, reader.IsDBNull(1) ? null : Fingerprint(reader.GetString(1)), reader.GetInt32(2) != 0,
            reader.IsDBNull(3) ? null : Fingerprint(reader.GetString(3)), reader.IsDBNull(4) ? null : Date(reader.GetString(4)),
            reader.IsDBNull(5) ? null : Fingerprint(reader.GetString(5)), reader.GetInt32(6) != 0);
    }
    public PeerTrustRecord? Read(Guid peer)
    {
        Id(peer); using var c = Open(); using var tx = c.BeginTransaction(deferred: true); RequireHost(c, tx); return Read(c, tx, peer);
    }
    // Handshake admission only. A later RPC must additionally bind the claimed Host UUID
    // to this actual fingerprint and recheck state; a key alone is not peer authority.
    public bool RecognizesTransportFingerprint(string fingerprint)
    {
        Fingerprint(fingerprint); using var c = Open(); using var tx = c.BeginTransaction(deferred: true); RequireHost(c, tx);
        using var command = Command(c, tx, """
            SELECT t.State,p.LocalBoundPublicKeyFingerprint,p.ExpiresUtc FROM TrustedManagers t
            LEFT JOIN TrustedManagerPairings p ON p.PeerHostId=t.PeerHostId
            WHERE t.State IN ('PeerBound','Active') AND t.PeerRecoveryRequired=0 AND
                (t.CurrentTrustedPublicKeyFingerprint=$fp OR t.PendingTrustedPublicKeyFingerprint=$fp);
            """, ("$fp", fingerprint));
        using var reader = command.ExecuteReader(); var now = time.GetUtcNow();
        while (reader.Read())
            if (reader.GetString(0) == "Active" || (!reader.IsDBNull(1) && !reader.IsDBNull(2) && Date(reader.GetString(2)) > now)) return true;
        return false;
    }
    public PeerBindingResult RecordVerifiedBinding(Guid peer, string peerFingerprint, string verifiedLocalFingerprint)
    {
        Id(peer); Fingerprint(peerFingerprint); Fingerprint(verifiedLocalFingerprint);
        if (peer == hostId) throw new ArgumentException("A Host cannot pair with itself.");
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        if (RequireHost(c, tx) != verifiedLocalFingerprint) throw new InvalidOperationException("Local credential changed during pairing.");
        var now = time.GetUtcNow(); Expire(c, tx, now);
        var existing = Read(c, tx, peer); PeerBindingResult result;
        if (existing is null)
        {
            var expires = now + PendingLifetime;
            Execute(c, tx, """
                INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc)
                VALUES ($peer,'PeerBound',$fp,$now);
                INSERT INTO TrustedManagerPairings (PeerHostId,BoundUtc,ExpiresUtc,LocalBoundPublicKeyFingerprint)
                VALUES ($peer,$now,$expires,$local);
                """, ("$peer", Id(peer)), ("$fp", peerFingerprint), ("$now", Stamp(now)), ("$expires", Stamp(expires)), ("$local", verifiedLocalFingerprint));
            Audit(c, tx, peer, "PeerBoundCreated", now); result = new(PeerBindingDisposition.PeerBoundCreated, peer, expires);
        }
        else if (existing.CurrentFingerprint == peerFingerprint && existing.State != "Revoked")
        {
            if (existing.RecoveryRequired) result = new(PeerBindingDisposition.RecoveryRequired, peer, existing.ExpiresUtc);
            else if (existing.State == "Active") result = new(PeerBindingDisposition.ActiveReconfirmed, peer, null);
            else
            {
                if (existing.LocalBoundFingerprint is null || existing.ExpiresUtc is null)
                    throw new InvalidDataException("Pending trust lacks verified binding metadata.");
                result = new(PeerBindingDisposition.ResumePeerBound, peer, existing.ExpiresUtc);
            }
        }
        else result = Candidate(c, tx, existing, peerFingerprint, now);
        tx.Commit(); return result;
    }
    private PeerBindingResult Candidate(SqliteConnection c, SqliteTransaction tx, PeerTrustRecord existing, string fingerprint, DateTimeOffset now)
    {
        using (var command = Command(c, tx, """
            SELECT ReplacementId,ExpiresUtc,ExpectedTrustState,ExpectedCurrentTrustedPublicKeyFingerprint
            FROM PendingCredentialReplacements WHERE PeerHostId=$peer AND ProposedKeyFingerprint=$fp
                AND InvalidatedUtc IS NULL AND ApprovedUtc IS NULL;
            """, ("$peer", Id(existing.PeerHostId)), ("$fp", fingerprint)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                if (Date(reader.GetString(1)) > now && reader.GetString(2) == existing.State &&
                    (reader.IsDBNull(3) ? null : reader.GetString(3)) == existing.CurrentFingerprint)
                    return new(PeerBindingDisposition.ReplacementRequired, existing.PeerHostId, Date(reader.GetString(1)), Guid.Parse(reader.GetString(0)));
        }
        var id = Guid.NewGuid(); var expires = now + PendingLifetime;
        Execute(c, tx, """
            INSERT INTO PendingCredentialReplacements (ReplacementId,PeerHostId,ProposedKeyFingerprint,VerifiedUtc,ExpiresUtc,
                ExpectedTrustState,ExpectedCurrentTrustedPublicKeyFingerprint,CreatedUtc)
            VALUES ($id,$peer,$fp,$now,$expires,$state,$old,$now);
            """, ("$id", Id(id)), ("$peer", Id(existing.PeerHostId)), ("$fp", fingerprint), ("$now", Stamp(now)),
            ("$expires", Stamp(expires)), ("$state", existing.State), ("$old", existing.CurrentFingerprint));
        Audit(c, tx, existing.PeerHostId, "PeerCredentialReplacementPending", now);
        return new(PeerBindingDisposition.ReplacementRequired, existing.PeerHostId, expires, id);
    }
    public int ExpirePending()
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false); RequireHost(c, tx);
        var count = Expire(c, tx, time.GetUtcNow()); tx.Commit(); return count;
    }
    private int Expire(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now)
    {
        var expired = new List<Guid>();
        using (var command = Command(c, tx, """
            SELECT t.PeerHostId,p.ExpiresUtc FROM TrustedManagers t
            LEFT JOIN TrustedManagerPairings p ON p.PeerHostId=t.PeerHostId WHERE t.State='PeerBound';
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.IsDBNull(1)) throw new InvalidDataException("Pending trust lacks a bounded deadline.");
                if (Date(reader.GetString(1)) <= now) expired.Add(Guid.Parse(reader.GetString(0)));
            }
        }
        foreach (var peer in expired)
        {
            Execute(c, tx, """
                UPDATE TrustedManagers SET State='Revoked',CurrentTrustedPublicKeyFingerprint=NULL,
                    PendingTrustedPublicKeyFingerprint=NULL,PendingRotationId=NULL,PendingRotationExpiresUtc=NULL,
                    PendingReconfirmationRequired=0,PeerRecoveryRequired=0,RevokedUtc=$now WHERE PeerHostId=$peer;
                UPDATE PendingCredentialReplacements SET InvalidatedUtc=$now
                    WHERE PeerHostId=$peer AND InvalidatedUtc IS NULL;
                """, ("$peer", Id(peer)), ("$now", Stamp(now)));
            Audit(c, tx, peer, "PeerBoundExpired", now);
        }
        return expired.Count;
    }
    private void Audit(SqliteConnection c, SqliteTransaction tx, Guid peer, string kind, DateTimeOffset now)
        => Execute(c, tx, """
            INSERT INTO AuditEvents (AuditEventId,OccurredUtc,EventKind,ActorKind,ActorPeerHostId,AffectedHostId,Summary)
            VALUES ($id,$now,$kind,$actorKind,$actorPeer,$host,$summary);
            """, ("$id", Id(Guid.NewGuid())), ("$now", Stamp(now)), ("$kind", kind),
            ("$actorKind", kind == "PeerBoundExpired" ? null : "RemoteManager"),
            ("$actorPeer", kind == "PeerBoundExpired" ? null : Id(peer)), ("$host", Id(hostId)),
            ("$summary", $"{kind}: peer {Id(peer)}."));
}
