using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed record RoutineRotationPeer(Guid PeerHostId, string State, string CurrentFingerprint,
    bool RecoveryRequired, string? LocalBoundFingerprint, DateTimeOffset? PairingExpiresUtc);
// Public read-only context, never proof of acknowledgement or permission to cut over.
public sealed record RoutineRotationPeerSet(HostRotationProposal Proposal, long Revision, IReadOnlyList<RoutineRotationPeer> Peers);

public sealed partial class HostCredentialStateRepository
{
    private static long PeerRevision(SqliteConnection c, SqliteTransaction tx)
    {
        using var command = Command(c, tx, "SELECT Revision FROM PeerTrustRevision WHERE Id=1;");
        return command.ExecuteScalar() is long revision && revision >= 0
            ? revision : throw new InvalidDataException("Peer trust revision unavailable.");
    }
    public RoutineRotationPeerSet ReadRoutineRotationPeerSet(Guid rotationId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true);
        var revision = PeerRevision(c, tx);
        var pins = ProposalPins(Read(c, tx), rotationId);
        var proposal = ReadProposal(c, tx, rotationId, pins) ?? throw new InvalidOperationException("Rotation proposal has not been prepared.");
        var peers = new List<RoutineRotationPeer>();
        using var command = Command(c, tx, """
            SELECT t.PeerHostId,t.State,t.CurrentTrustedPublicKeyFingerprint,t.PeerRecoveryRequired,
                p.LocalBoundPublicKeyFingerprint,p.ExpiresUtc
            FROM TrustedManagers t LEFT JOIN TrustedManagerPairings p ON p.PeerHostId=t.PeerHostId
            WHERE t.State<>'Revoked' ORDER BY t.PeerHostId;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Guid.TryParseExact(reader.GetString(0), "D", out var peer) || peer == Guid.Empty || peer == _hostId ||
                reader.GetString(1) is not ("Active" or "PeerBound") || reader.IsDBNull(2) || !HostTrustPlanning.Fingerprint(reader.GetString(2)) ||
                reader.GetInt64(3) is not (0 or 1)) throw new InvalidDataException("Invalid peer-set metadata.");
            var local = reader.IsDBNull(4) ? null : reader.GetString(4);
            if (local is not null && !HostTrustPlanning.Fingerprint(local)) throw new InvalidDataException("Invalid local pairing fingerprint.");
            DateTimeOffset? expires = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture);
            if (reader.GetString(1) == "PeerBound" && expires is null) throw new InvalidDataException("Pending peer lacks its bounded deadline.");
            peers.Add(new(peer, reader.GetString(1), reader.GetString(2), reader.GetInt64(3) == 1, local, expires));
        }
        return new(proposal, revision, peers.AsReadOnly());
    }
}
