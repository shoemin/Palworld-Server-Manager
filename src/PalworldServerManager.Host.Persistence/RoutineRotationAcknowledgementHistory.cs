using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed partial class HostCredentialStateRepository
{
    // Historical evidence of a peer's durable acceptance, never a fresh cutover predicate.
    // The future cutover engine must revalidate all remaining peers and bounded live leases.
    public bool RecordRoutineRotationPeerAcknowledgement(HostRotationProposal proposal, Guid peer, string actualLocalFingerprint, string actualPeerFingerprint)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (peer == Guid.Empty || peer == _hostId || proposal.HostId != _hostId ||
            !HostTrustPlanning.Fingerprint(actualPeerFingerprint) || actualLocalFingerprint != proposal.OldFingerprint) throw RoutineDenied();
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        var pins = ProposalPins(Read(c, tx), proposal.RotationId);
        if (ReadProposal(c, tx, proposal.RotationId, pins) != proposal) throw RoutineDenied();
        using (var command = Command(c, tx, """
            SELECT COUNT(*) FROM TrustedManagers WHERE PeerHostId=$peer AND State='Active' AND PeerRecoveryRequired=0
                AND CurrentTrustedPublicKeyFingerprint=$fingerprint;
            """, ("$peer", peer.ToString("D")), ("$fingerprint", actualPeerFingerprint)))
            if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw RoutineDenied();
        using (var command = Command(c, tx, "SELECT AcknowledgedUtc,PromotedUtc FROM HostCredentialRotationPeers WHERE RotationId=$rotation AND PeerHostId=$peer;",
            ("$rotation", proposal.RotationId.ToString("D")), ("$peer", peer.ToString("D"))))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (!reader.IsDBNull(1)) throw new InvalidDataException("Pre-cutover peer history unexpectedly contains promotion.");
                if (!reader.IsDBNull(0)) return false;
            }
        }
        var now = DateTimeOffset.UtcNow.ToString("O");
        Execute(c, tx, """
            INSERT INTO HostCredentialRotationPeers (RotationId,PeerHostId,StagedUtc,AcknowledgedUtc)
                VALUES ($rotation,$peer,$now,$now)
                ON CONFLICT(RotationId,PeerHostId) DO UPDATE SET StagedUtc=COALESCE(StagedUtc,$now),AcknowledgedUtc=$now;
            INSERT INTO AuditEvents (AuditEventId,OccurredUtc,EventKind,ActorKind,ActorPeerHostId,AffectedHostId,Summary)
                VALUES ($event,$now,'HostRotationPeerAcknowledged','RemoteManager',$peer,$host,$summary);
            """, ("$rotation", proposal.RotationId.ToString("D")), ("$peer", peer.ToString("D")), ("$now", now),
            ("$event", Guid.NewGuid().ToString("D")), ("$host", _hostId.ToString("D")), ("$summary", $"Peer accepted rotation {proposal.RotationId:D}."));
        tx.Commit(); return true;
    }
}
