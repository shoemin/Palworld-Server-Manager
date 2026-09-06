using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed partial class LocalEnrollmentRepository
{
    // Public identity only, for the privileged offline caller's intended-user rotation handoff.
    // Preparation still captures/rechecks the actual Owner inside its own writer transaction.
    public (Guid LocalPrincipalId, string OsPrincipalRef) ReadOfflineOwnerIdentity()
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true); RequireState(c, tx, true);
        var owner = ReadOwner(c, tx); return (Guid.Parse(owner.Id), owner.Native);
    }
    // Both preparation methods are trusted OFFLINE persistence seams. Their executable caller
    // must enforce actual Administrator privilege, stopped Host and the machine lease (42d2).
    // Neither is exposed by LocalEnrollmentService or ordinary/remote RPC.
    public void PrepareOfflineOwnerRotation(Guid ticketId, LocalEnrollmentVerifier verifier, DateTimeOffset expiresUtc)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false); RequireState(c, tx, true);
        var now = _time.GetUtcNow(); RecoveryDeadline(now, expiresUtc); var owner = ReadOwner(c, tx);
        Execute(c, tx, """
            INSERT INTO PendingOwnerCredentialRotations (RotationTicketId,LocalPrincipalId,OsPrincipalRef,SecretVerifier,
                ExpectedCurrentPublicVerificationKey,ExpiresUtc,CreatedUtc) VALUES ($id,$owner,$os,$verifier,$key,$expires,$now);
            """, ("$id", Id(ticketId)), ("$owner", owner.Id), ("$os", owner.Native), ("$verifier", verifier.ExportForPersistence()),
            ("$key", owner.Key), ("$expires", Stamp(expiresUtc)), ("$now", Stamp(now)));
        Audit(c, tx, "OwnerCredentialRotationPrepared", null, true, "Owner credential rotation prepared.", now); tx.Commit();
    }
    public void PrepareOfflineOwnerRehome(Guid ticketId, string newNativePrincipal, LocalEnrollmentVerifier verifier, DateTimeOffset expiresUtc)
    {
        var native = Native(newNativePrincipal);
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false); RequireState(c, tx, true);
        var now = _time.GetUtcNow(); RecoveryDeadline(now, expiresUtc); var owner = ReadOwner(c, tx);
        if (owner.Native == native) throw Denied();
        var target = RecoveryTarget(c, tx, native);
        Execute(c, tx, """
            INSERT INTO PendingOwnerRehomes (RehomeTicketId,NewOsPrincipalRef,SecretVerifier,ExpectedCurrentOwnerLocalPrincipalId,
                ExpectedCurrentOwnerPublicVerificationKey,ExpectedTargetLocalPrincipalId,ExpectedTargetState,ExpectedTargetPublicVerificationKey,ExpiresUtc,CreatedUtc)
            VALUES ($id,$os,$verifier,$owner,$ownerKey,$target,$state,$key,$expires,$now);
            """, ("$id", Id(ticketId)), ("$os", native), ("$verifier", verifier.ExportForPersistence()), ("$owner", owner.Id), ("$ownerKey", owner.Key),
            ("$target", target?.Id), ("$state", target?.State), ("$key", target?.Key), ("$expires", Stamp(expiresUtc)), ("$now", Stamp(now)));
        Audit(c, tx, "OwnerRehomePrepared", null, true, "Owner re-home prepared.", now); tx.Commit();
    }
    public Guid CompleteOwnerRotation(Guid ticketId, string nativePrincipal, LocalEnrollmentVerifier presented, string publicKey)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        var ticket = ReadRecovery(c, tx, false, Id(ticketId)); var now = _time.GetUtcNow();
        if (ticket is null || ticket.Native != Native(nativePrincipal) || !presented.MatchesPersisted(ticket.Verifier)) throw Denied();
        if (ticket.Consumed) return RecoveryId(ticket.OwnerId); // prior fact, not another key replacement
        if (ticket.Invalidated || ticket.Expires <= now || !LocalPrincipalProof.IsValidPublicKey(publicKey)) throw Denied();
        RequireState(c, tx, true); var owner = ReadOwner(c, tx);
        if (owner.Id != ticket.OwnerId || owner.Native != ticket.Native || owner.Key != ticket.OwnerKey || publicKey == owner.Key) throw Denied();
        var principalId = RecoveryId(owner.Id);
        Execute(c, tx, "UPDATE LocalPrincipals SET PublicVerificationKey=$key WHERE LocalPrincipalId=$id;", ("$key", publicKey), ("$id", owner.Id));
        Execute(c, tx, "UPDATE PendingOwnerCredentialRotations SET ConsumedUtc=$now WHERE RotationTicketId=$id;", ("$now", Stamp(now)), ("$id", Id(ticketId)));
        InvalidateDependentTickets(c, tx, principalId, owner.Native, now);
        Audit(c, tx, "OwnerCredentialRotationCompleted", principalId, false, "Owner credential rotation completed.", now);
        tx.Commit(); return principalId;
    }
    public Guid CompleteOwnerRehome(Guid ticketId, string nativePrincipal, LocalEnrollmentVerifier presented, string publicKey)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        var ticket = ReadRecovery(c, tx, true, Id(ticketId)); var now = _time.GetUtcNow();
        if (ticket is null || ticket.Native != Native(nativePrincipal) || !presented.MatchesPersisted(ticket.Verifier)) throw Denied();
        if (ticket.Consumed) return RecoveryId(ticket.Result);
        if (ticket.Invalidated || ticket.Expires <= now || !LocalPrincipalProof.IsValidPublicKey(publicKey)) throw Denied();
        RequireState(c, tx, true); var owner = ReadOwner(c, tx); var target = RecoveryTarget(c, tx, ticket.Native);
        if (owner.Id != ticket.OwnerId || owner.Key != ticket.OwnerKey || owner.Native == ticket.Native ||
            target?.Id != ticket.TargetId || target?.State != ticket.TargetState || target?.Key != ticket.TargetKey) throw Denied();
        // Already-enrolled active replacement keeps its existing credential; revocation cleared
        // a tombstone's key, so that case (like a new row) necessarily registers a fresh key.
        if (target is { State: "Active" } && publicKey != target.Value.Key) throw Denied();
        var oldOwnerId = RecoveryId(owner.Id); var result = target is { } existing ? RecoveryId(existing.Id) : Guid.NewGuid();
        Execute(c, tx, "UPDATE LocalPrincipals SET IsOwner=0,State='Revoked',PublicVerificationKey=NULL,RevokedUtc=$now WHERE LocalPrincipalId=$id;",
            ("$now", Stamp(now)), ("$id", owner.Id));
        InvalidateGrants(c, tx, oldOwnerId, now);
        if (target is null)
            Execute(c, tx, """
                INSERT INTO LocalPrincipals (LocalPrincipalId,OsPrincipalRef,PublicVerificationKey,IsOwner,State,CreatedUtc)
                VALUES ($id,$os,$key,1,'Active',$now);
                """, ("$id", Id(result)), ("$os", ticket.Native), ("$key", publicKey), ("$now", Stamp(now)));
        else
            Execute(c, tx, "UPDATE LocalPrincipals SET IsOwner=1,State='Active',PublicVerificationKey=$key,RevokedUtc=NULL WHERE LocalPrincipalId=$id;",
                ("$key", publicKey), ("$id", Id(result)));
        Execute(c, tx, "UPDATE PendingOwnerRehomes SET ConsumedUtc=$now,ResultLocalPrincipalId=$result WHERE RehomeTicketId=$id;",
            ("$now", Stamp(now)), ("$result", Id(result)), ("$id", Id(ticketId)));
        InvalidateDependentTickets(c, tx, oldOwnerId, owner.Native, now);
        InvalidateDependentTickets(c, tx, result, ticket.Native, now);
        RequireState(c, tx, true);
        Audit(c, tx, "OwnerRehomeCompleted", result, false, "Owner re-home completed; prior Owner revoked: " + owner.Id, now);
        tx.Commit(); return result;
    }
    private static void RecoveryDeadline(DateTimeOffset now, DateTimeOffset expires)
    { if (expires <= now || expires > now.AddHours(24)) throw new ArgumentException("Recovery expiration must be bounded."); }
    private static Guid RecoveryId(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw Denied();
    private static (string Id, string Native, string Key) ReadOwner(SqliteConnection c, SqliteTransaction tx)
    {
        using var command = Command(c, tx, "SELECT LocalPrincipalId,OsPrincipalRef,PublicVerificationKey FROM LocalPrincipals WHERE IsOwner=1 AND State='Active';");
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw Denied();
        var key = reader.GetString(2); if (!LocalPrincipalProof.IsValidPublicKey(key)) throw Denied();
        return (reader.GetString(0), reader.GetString(1), key);
    }
    private static (string Id, string State, string? Key)? RecoveryTarget(SqliteConnection c, SqliteTransaction tx, string native)
    {
        using var command = Command(c, tx, "SELECT LocalPrincipalId,State,PublicVerificationKey,IsOwner FROM LocalPrincipals WHERE OsPrincipalRef=$os;", ("$os", native));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        if (reader.GetInt32(3) != 0) throw Denied();
        return (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }
    private sealed class RecoveryTicket
    {
        internal required string Native, Verifier, OwnerId, OwnerKey;
        internal string? TargetId, TargetState, TargetKey, Result;
        internal DateTimeOffset Expires; internal bool Consumed, Invalidated;
        public override string ToString() => "[REDACTED recovery ticket]";
    }
    private static RecoveryTicket? ReadRecovery(SqliteConnection c, SqliteTransaction tx, bool rehome, string id)
    {
        using var command = Command(c, tx, rehome ? """
            SELECT NewOsPrincipalRef,SecretVerifier,ExpectedCurrentOwnerLocalPrincipalId,ExpectedCurrentOwnerPublicVerificationKey,
                ExpectedTargetLocalPrincipalId,ExpectedTargetState,ExpectedTargetPublicVerificationKey,ResultLocalPrincipalId,ExpiresUtc,ConsumedUtc,InvalidatedUtc
            FROM PendingOwnerRehomes WHERE RehomeTicketId=$id;
            """ : """
            SELECT OsPrincipalRef,SecretVerifier,LocalPrincipalId,ExpectedCurrentPublicVerificationKey,NULL,NULL,NULL,NULL,ExpiresUtc,ConsumedUtc,InvalidatedUtc
            FROM PendingOwnerCredentialRotations WHERE RotationTicketId=$id;
            """, ("$id", id));
        using var r = command.ExecuteReader(); if (!r.Read()) return null;
        return new() { Native=r.GetString(0), Verifier=r.GetString(1), OwnerId=r.GetString(2), OwnerKey=r.GetString(3),
            TargetId=r.IsDBNull(4)?null:r.GetString(4), TargetState=r.IsDBNull(5)?null:r.GetString(5), TargetKey=r.IsDBNull(6)?null:r.GetString(6),
            Result=r.IsDBNull(7)?null:r.GetString(7), Expires=ParseTime(r.GetString(8)), Consumed=!r.IsDBNull(9), Invalidated=!r.IsDBNull(10) };
    }
}
