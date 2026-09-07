using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence;

internal static class PeerRelationshipIncarnation
{
    internal static long Read(SqliteConnection connection, SqliteTransaction transaction, Guid peer)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId=$peer;";
        command.Parameters.AddWithValue("$peer", peer.ToString("D"));
        return command.ExecuteScalar() is long incarnation && incarnation > 0
            ? incarnation : throw new InvalidDataException("Peer relationship incarnation unavailable.");
    }
}

public sealed partial class PeerTrustRepository
{
    // Capture at authenticated negotiation. This local revision is never supplied on the wire.
    public long ReadAuthenticatedRelationshipIncarnation(Guid peer, string actualPeerFingerprint, string actualLocalFingerprint)
    {
        Id(peer); Fingerprint(actualPeerFingerprint); Fingerprint(actualLocalFingerprint);
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true);
        RequireActivationPeer(c, tx, peer, actualPeerFingerprint, actualLocalFingerprint, time.GetUtcNow());
        return PeerRelationshipIncarnation.Read(c, tx, peer);
    }
}
