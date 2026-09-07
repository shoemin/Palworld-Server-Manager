namespace PalworldServerManager.Host.Persistence;

public enum PairingTerminalOutcome { Failed = 1, Expired = 2 }

public sealed partial class PeerTrustRepository
{
    // Host-generated event identity; no claimed peer, address, exception text or cryptographic material.
    // The first terminal outcome wins when timer/disconnect paths race or a commit reply is lost.
    public void RecordPairingTerminal(Guid attemptId, PairingTerminalOutcome outcome, DateTimeOffset occurredUtc)
    {
        Id(attemptId);
        if (outcome is not (PairingTerminalOutcome.Failed or PairingTerminalOutcome.Expired)) throw new ArgumentException("Invalid terminal pairing outcome.");
        using var c = Open(1); using var tx = c.BeginTransaction(deferred: false); RequireHost(c, tx);
        using (var command = Command(c, tx, "SELECT EventKind,AffectedHostId,ActorKind,ActorPeerHostId,ActorLocalPrincipalId,Summary FROM AuditEvents WHERE AuditEventId=$id;", ("$id", Id(attemptId))))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                var kind = reader.GetString(0);
                if (kind is not ("PairingAttemptFailed" or "PairingAttemptExpired") || reader.IsDBNull(1) || reader.GetString(1) != Id(hostId) ||
                    !reader.IsDBNull(2) || !reader.IsDBNull(3) || !reader.IsDBNull(4) || reader.IsDBNull(5) ||
                    reader.GetString(5) != $"{kind}: attempt {Id(attemptId)}.") throw new InvalidDataException("Pairing audit identity conflict.");
                return;
            }
        }
        var eventKind = outcome == PairingTerminalOutcome.Expired ? "PairingAttemptExpired" : "PairingAttemptFailed";
        Execute(c, tx, """
            INSERT INTO AuditEvents (AuditEventId,OccurredUtc,EventKind,AffectedHostId,Summary)
            VALUES ($id,$now,$kind,$host,$summary);
            """, ("$id", Id(attemptId)), ("$now", Stamp(occurredUtc)), ("$kind", eventKind), ("$host", Id(hostId)),
            ("$summary", $"{eventKind}: attempt {Id(attemptId)}."));
        tx.Commit();
    }

    public int MaintainPendingPairingTrust()
    {
        using var c = Open(1); using var tx = c.BeginTransaction(deferred: false); RequireHost(c, tx);
        var now = time.GetUtcNow(); var count = Expire(c, tx, now); ExpireRotations(c, tx, now); tx.Commit(); return count;
    }
}
