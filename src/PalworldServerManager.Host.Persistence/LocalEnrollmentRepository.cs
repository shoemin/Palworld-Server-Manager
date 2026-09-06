using System.Globalization;
using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

// Caller retains the machine-wide Host/offline lease. Each operation owns an immediate writer
// transaction and a non-pooled connection. Raw bearer material never crosses this API.
public sealed partial class LocalEnrollmentRepository(HostDatabase database, Guid hostId, TimeProvider? timeProvider = null)
{
    private readonly HostDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly Guid _hostId = hostId != Guid.Empty ? hostId : throw new ArgumentException("Host identity required.");
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private static AuthenticationException Denied() => new("The local enrollment operation was refused.");
    private static string Id(Guid value) => value != Guid.Empty ? value.ToString("D") : throw new ArgumentException("Identity required.");
    private static string Native(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 ? value : throw new ArgumentException("Native identity required.");
    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture);
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _database.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = true }.ToString());
        try { c.Open(); return c; } catch { c.Dispose(); throw; }
    }
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] values)
    {
        var command = c.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] values)
    { using var command = Command(c, tx, sql, values); command.ExecuteNonQuery(); }
    private void RequireState(SqliteConnection c, SqliteTransaction tx, bool initialized)
    {
        using var command = Command(c, tx, "SELECT HostId, HostBootstrapState FROM HostIdentity WHERE Id=1;");
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != Id(_hostId) || reader.GetString(1) != (initialized ? "Initialized" : "Uninitialized")) throw Denied();
        reader.Close();
        if (HostIdentityRepository.CountActiveOwners(c, tx) != (initialized ? 1 : 0)) throw Denied();
    }
    private void RequireOwner(SqliteConnection c, SqliteTransaction tx, LocalPrincipalMutationActor actor)
    {
        RequireState(c, tx, true);
        if (actor.HostId != _hostId) throw Denied();
        using var command = Command(c, tx, """
            SELECT COUNT(*) FROM LocalPrincipals WHERE LocalPrincipalId=$id AND OsPrincipalRef=$os
                AND PublicVerificationKey=$key AND State='Active' AND IsOwner=1;
            """, ("$id", Id(actor.LocalPrincipalId)), ("$os", Native(actor.OsPrincipalRef)), ("$key", actor.PublicVerificationKey));
        if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw Denied();
    }
    private static (string Id, string State, bool Owner)? Principal(SqliteConnection c, SqliteTransaction tx, string native)
    {
        using var command = Command(c, tx, "SELECT LocalPrincipalId, State, IsOwner FROM LocalPrincipals WHERE OsPrincipalRef=$os;", ("$os", native));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1), reader.GetInt32(2) == 1) : null;
    }
    private void Audit(SqliteConnection c, SqliteTransaction tx, string kind, Guid? actor, bool offline, string summary, DateTimeOffset now)
        => Execute(c, tx, """
            INSERT INTO AuditEvents (AuditEventId, OccurredUtc, EventKind, ActorKind, ActorLocalPrincipalId,
                AffectedHostId, IsOfflineRecovery, Summary) VALUES ($id,$now,$kind,$actorKind,$actor,$host,$offline,$summary);
            """, ("$id", Id(Guid.NewGuid())), ("$now", Stamp(now)), ("$kind", kind),
            ("$actorKind", offline ? "OfflineRecovery" : "LocalPrincipal"), ("$actor", actor?.ToString("D")),
            ("$host", Id(_hostId)), ("$offline", offline ? 1 : 0), ("$summary", summary));

    // Persistence primitive ONLY for privileged offline preparation. The executable caller must
    // check actual Administrator privilege and own the exclusivity lease before invoking this.
    // No online Host adapter exposes it. Secure key and intended-user handoff composition is 42d.
    public void PrepareOfflineBootstrap(Guid ticketId, string intendedNativePrincipal, LocalEnrollmentVerifier verifier, DateTimeOffset expiresUtc)
    {
        var id = Id(ticketId); var native = Native(intendedNativePrincipal);
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false); RequireState(c, tx, false);
        var now = _time.GetUtcNow();
        if (expiresUtc <= now || expiresUtc > now.AddHours(24)) throw new ArgumentException("Bootstrap expiration must be bounded.");
        // Only an expired predecessor can be superseded; a live authorization is never silently retargeted.
        using (var existing = Command(c, tx, "SELECT ExpiresUtc FROM PendingOwnerEnrollments WHERE ConsumedUtc IS NULL AND InvalidatedUtc IS NULL;"))
        { var deadline = existing.ExecuteScalar() as string; if (deadline is not null && ParseTime(deadline) > now) throw Denied(); }
        Execute(c, tx, "UPDATE PendingOwnerEnrollments SET InvalidatedUtc=$now WHERE ConsumedUtc IS NULL AND InvalidatedUtc IS NULL;", ("$now", Stamp(now)));
        Execute(c, tx, """
            INSERT INTO PendingOwnerEnrollments (PendingOwnerEnrollmentId,OsPrincipalRef,SecretVerifier,ExpiresUtc,CreatedUtc)
            VALUES ($id,$os,$verifier,$expires,$now);
            """, ("$id", id), ("$os", native), ("$verifier", verifier.ExportForPersistence()), ("$expires", Stamp(expiresUtc)), ("$now", Stamp(now)));
        Audit(c, tx, "OwnerBootstrapPrepared", null, true, "Initial Owner bootstrap prepared.", now); tx.Commit();
    }

    public Guid CompleteBootstrap(Guid ticketId, string nativePrincipal, LocalEnrollmentVerifier presented, string publicKey)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        var ticket = ReadTicket(c, tx, true, Id(ticketId)); var now = _time.GetUtcNow();
        if (ticket is null || ticket.Native != Native(nativePrincipal) || !presented.MatchesPersisted(ticket.Verifier)) throw Denied();
        if (ticket.Consumed) return RecordedResult(ticket); // no expiry/state/key mutation on retries
        if (ticket.Invalidated || ticket.Expires <= now || !LocalPrincipalProof.IsValidPublicKey(publicKey)) throw Denied();
        RequireState(c, tx, false);
        if (Principal(c, tx, ticket.Native) is not null) throw Denied();
        var principalId = Guid.NewGuid();
        new HostIdentityRepository(_database).InitializeWithOwner(c, tx, Id(principalId), ticket.Native, publicKey);
        Consume(c, tx, true, ticketId, principalId, now);
        Audit(c, tx, "OwnerBootstrapCompleted", principalId, false, "Initial Owner bootstrap completed.", now);
        tx.Commit(); return principalId;
    }

    public void CreateEnrollment(LocalPrincipalMutationActor owner, Guid ticketId, string intendedNativePrincipal,
        LocalEnrollmentVerifier verifier, DateTimeOffset expiresUtc)
    {
        var native = Native(intendedNativePrincipal);
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false); RequireOwner(c, tx, owner);
        var now = _time.GetUtcNow();
        if (expiresUtc <= now || expiresUtc > now.AddHours(24)) throw new ArgumentException("Enrollment expiration must be bounded.");
        var target = Principal(c, tx, native);
        if (target is { } row && (row.State != "Revoked" || row.Owner)) throw Denied();
        Execute(c, tx, """
            INSERT INTO PendingLocalPrincipalEnrollments (EnrollmentId,OsPrincipalRef,EnrollmentCodeVerifier,TargetLocalPrincipalId,
                CreatedByOwnerLocalPrincipalId,ExpiresUtc,CreatedUtc) VALUES ($id,$os,$verifier,$target,$owner,$expires,$now);
            """, ("$id", Id(ticketId)), ("$os", native), ("$verifier", verifier.ExportForPersistence()), ("$target", target?.Id),
            ("$owner", Id(owner.LocalPrincipalId)), ("$expires", Stamp(expiresUtc)), ("$now", Stamp(now)));
        Audit(c, tx, "LocalPrincipalEnrollmentPrepared", owner.LocalPrincipalId, false, "Local principal enrollment authorized.", now); tx.Commit();
    }

    public Guid CompleteEnrollment(Guid ticketId, string nativePrincipal, LocalEnrollmentVerifier presented, string publicKey)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        var ticket = ReadTicket(c, tx, false, Id(ticketId)); var now = _time.GetUtcNow();
        if (ticket is null) throw Denied();
        var correct = presented.MatchesPersisted(ticket.Verifier);
        if (!correct)
        {
            // Count every wrong-code presentation. Consumed/expired/invalid records confer no new
            // guesses; cap the counter, and never let it interfere with a correct consumed retry.
            if (!ticket.Consumed && !ticket.Invalidated && ticket.Expires > now && ticket.Attempts < 10)
            {
                Execute(c, tx, "UPDATE PendingLocalPrincipalEnrollments SET FailedAttempts=FailedAttempts+1 WHERE EnrollmentId=$id;", ("$id", Id(ticketId)));
                tx.Commit();
            }
            throw Denied();
        }
        if (ticket.Native != Native(nativePrincipal)) throw Denied();
        if (ticket.Consumed) return RecordedResult(ticket);
        if (ticket.Invalidated || ticket.Expires <= now || ticket.Attempts >= 10 || !LocalPrincipalProof.IsValidPublicKey(publicKey)) throw Denied();
        RequireState(c, tx, true);
        var target = Principal(c, tx, ticket.Native);
        Guid principalId;
        if (ticket.Target is null)
        {
            if (target is not null) throw Denied();
            principalId = Guid.NewGuid();
            Execute(c, tx, """
                INSERT INTO LocalPrincipals (LocalPrincipalId,OsPrincipalRef,PublicVerificationKey,IsOwner,State,CreatedUtc)
                VALUES ($id,$os,$key,0,'Active',$now);
                """, ("$id", Id(principalId)), ("$os", ticket.Native), ("$key", publicKey), ("$now", Stamp(now)));
        }
        else
        {
            if (target is not { } row || row.Id != ticket.Target || row.State != "Revoked" || row.Owner) throw Denied();
            principalId = Guid.Parse(row.Id);
            Execute(c, tx, "UPDATE LocalPrincipals SET State='Active', PublicVerificationKey=$key, RevokedUtc=NULL WHERE LocalPrincipalId=$id;",
                ("$key", publicKey), ("$id", Id(principalId)));
        }
        Consume(c, tx, false, ticketId, principalId, now);
        InvalidateDependentTickets(c, tx, principalId, ticket.Native, now);
        Audit(c, tx, "LocalPrincipalEnrollmentCompleted", principalId, false, "Local principal enrollment completed.", now);
        tx.Commit(); return principalId;
    }

    public void RevokePrincipal(LocalPrincipalMutationActor owner, Guid targetId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false); RequireOwner(c, tx, owner);
        var now = _time.GetUtcNow(); string native;
        using (var command = Command(c, tx, "SELECT OsPrincipalRef,IsOwner,State FROM LocalPrincipals WHERE LocalPrincipalId=$id;", ("$id", Id(targetId))))
        {
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetInt32(1) != 0) throw Denied();
            native = reader.GetString(0);
            if (reader.GetString(2) == "Revoked") return; // repeated revocation does not revoke a later fresh ticket
        }
        Execute(c, tx, "UPDATE LocalPrincipals SET State='Revoked',PublicVerificationKey=NULL,RevokedUtc=$now WHERE LocalPrincipalId=$id;",
            ("$now", Stamp(now)), ("$id", Id(targetId)));
        InvalidateDependentTickets(c, tx, targetId, native, now);
        InvalidateGrants(c, tx, targetId, now);
        Audit(c, tx, "LocalPrincipalRevoked", owner.LocalPrincipalId, false, "Local principal revoked: " + Id(targetId), now);
        tx.Commit();
    }

    // Shared transaction primitive for the later bounded offline recovery coordinator too.
    internal static void InvalidateDependentTickets(SqliteConnection c, SqliteTransaction tx, Guid principalId, string native, DateTimeOffset now)
    {
        Execute(c, tx, """
            UPDATE PendingLocalPrincipalEnrollments SET InvalidatedUtc=$now WHERE ConsumedUtc IS NULL AND InvalidatedUtc IS NULL
                AND (TargetLocalPrincipalId=$id OR OsPrincipalRef=$os);
            UPDATE PendingOwnerCredentialRotations SET InvalidatedUtc=$now WHERE ConsumedUtc IS NULL AND InvalidatedUtc IS NULL AND LocalPrincipalId=$id;
            UPDATE PendingOwnerRehomes SET InvalidatedUtc=$now WHERE ConsumedUtc IS NULL AND InvalidatedUtc IS NULL
                AND (ExpectedCurrentOwnerLocalPrincipalId=$id OR ExpectedTargetLocalPrincipalId=$id OR NewOsPrincipalRef=$os);
            """, ("$now", Stamp(now)), ("$id", Id(principalId)), ("$os", native));
    }
    internal static void InvalidateGrants(SqliteConnection c, SqliteTransaction tx, Guid principalId, DateTimeOffset now)
    {
        foreach (var table in new[] { "HostCapabilityGrants", "ServerCapabilityGrants" })
            Execute(c, tx, $"""
                WITH RECURSIVE affected(GrantId) AS (
                    SELECT GrantId FROM {table} WHERE GranteeLocalPrincipalId=$id
                    UNION SELECT g.GrantId FROM {table} g JOIN affected a ON g.DerivedFromGrantId=a.GrantId
                ) UPDATE {table} SET InvalidatedUtc=COALESCE(InvalidatedUtc,$now) WHERE GrantId IN (SELECT GrantId FROM affected);
                """, ("$id", Id(principalId)), ("$now", Stamp(now)));
    }
    private sealed class Ticket
    {
        internal required string Native, Verifier;
        internal string? Target, Result;
        internal DateTimeOffset Expires;
        internal bool Consumed, Invalidated;
        internal int Attempts;
        public override string ToString() => "[REDACTED enrollment ticket]";
    }
    private static Ticket? ReadTicket(SqliteConnection c, SqliteTransaction tx, bool bootstrap, string id)
    {
        using var command = Command(c, tx, bootstrap ? """
            SELECT OsPrincipalRef,SecretVerifier,ExpiresUtc,ConsumedUtc,ResultLocalPrincipalId,InvalidatedUtc,NULL,0
            FROM PendingOwnerEnrollments WHERE PendingOwnerEnrollmentId=$id;
            """ : """
            SELECT OsPrincipalRef,EnrollmentCodeVerifier,ExpiresUtc,ConsumedUtc,ResultLocalPrincipalId,InvalidatedUtc,TargetLocalPrincipalId,FailedAttempts
            FROM PendingLocalPrincipalEnrollments WHERE EnrollmentId=$id;
            """, ("$id", id));
        using var r = command.ExecuteReader(); if (!r.Read()) return null;
        return new() { Native=r.GetString(0), Verifier=r.GetString(1), Expires=ParseTime(r.GetString(2)), Consumed=!r.IsDBNull(3),
            Result=r.IsDBNull(4)?null:r.GetString(4), Invalidated=!r.IsDBNull(5), Target=r.IsDBNull(6)?null:r.GetString(6), Attempts=r.GetInt32(7) };
    }
    private static Guid RecordedResult(Ticket ticket) => Guid.TryParseExact(ticket.Result, "D", out var id) && id != Guid.Empty ? id : throw Denied();
    private static void Consume(SqliteConnection c, SqliteTransaction tx, bool bootstrap, Guid ticketId, Guid principalId, DateTimeOffset now)
        => Execute(c, tx, bootstrap ? "UPDATE PendingOwnerEnrollments SET ConsumedUtc=$now,ResultLocalPrincipalId=$result WHERE PendingOwnerEnrollmentId=$id;"
            : "UPDATE PendingLocalPrincipalEnrollments SET ConsumedUtc=$now,ResultLocalPrincipalId=$result WHERE EnrollmentId=$id;",
            ("$now", Stamp(now)), ("$result", Id(principalId)), ("$id", Id(ticketId)));
}
