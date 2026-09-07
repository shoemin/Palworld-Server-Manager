using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

// Public correlation data. The trusted adapter supplies both actual TLS fingerprints separately.
public sealed record RoutineRotationPromotionReceipt(Guid RequestId, Guid HostId, Guid RotationId, string NewFingerprint);

public sealed partial class HostCredentialStateRepository
{
    public bool RecordRoutineRotationPromotionReceipt(RoutineRotationPromotionReceipt receipt, Guid peer,
        string actualPeerFingerprint, string actualLocalFingerprint)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.RequestId == Guid.Empty || receipt.HostId != _hostId || receipt.RotationId == Guid.Empty ||
            peer == Guid.Empty || peer == _hostId || !HostTrustPlanning.Fingerprint(receipt.NewFingerprint) ||
            !HostTrustPlanning.Fingerprint(actualPeerFingerprint) || receipt.NewFingerprint != actualLocalFingerprint) throw RoutineDenied();
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        var snapshot = Read(c, tx); var plan = HostTrustPlanning.Build(snapshot);
        var rotation = snapshot.Rotations.SingleOrDefault(r => r.RotationId == receipt.RotationId);
        if (!snapshot.Initialized || plan.Publication?.CurrentFingerprint != actualLocalFingerprint || rotation is null ||
            rotation.State is not (HostCredentialRotationState.CutOver or HostCredentialRotationState.Completed) ||
            rotation.NewReference != snapshot.CurrentReference) throw RoutineDenied();
        using (var command = Command(c, tx, """
            SELECT COUNT(*) FROM TrustedManagers WHERE PeerHostId=$peer AND State='Active' AND PeerRecoveryRequired=0
                AND CurrentTrustedPublicKeyFingerprint=$fingerprint;
            """, ("$peer", peer.ToString("D")), ("$fingerprint", actualPeerFingerprint)))
            if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw RoutineDenied();
        using (var command = Command(c, tx, "SELECT PromotedUtc FROM HostCredentialRotationPeers WHERE RotationId=$rotation AND PeerHostId=$peer;",
            ("$rotation", receipt.RotationId.ToString("D")), ("$peer", peer.ToString("D"))))
            if (command.ExecuteScalar() is string) return false;
        var now = DateTimeOffset.UtcNow.ToString("O");
        Execute(c, tx, """
            INSERT INTO HostCredentialRotationPeers (RotationId,PeerHostId,PromotedUtc) VALUES ($rotation,$peer,$now)
                ON CONFLICT(RotationId,PeerHostId) DO UPDATE SET PromotedUtc=$now;
            INSERT INTO AuditEvents (AuditEventId,OccurredUtc,EventKind,ActorKind,ActorPeerHostId,AffectedHostId,Summary)
                VALUES ($event,$now,'HostRotationPeerPromotionReceived','RemoteManager',$peer,$host,$summary);
            """, ("$rotation", receipt.RotationId.ToString("D")), ("$peer", peer.ToString("D")), ("$now", now),
            ("$event", Guid.NewGuid().ToString("D")), ("$host", _hostId.ToString("D")), ("$summary", $"Peer confirmed promotion for rotation {receipt.RotationId:D}."));
        tx.Commit(); return true;
    }
}

public sealed partial class PeerTrustRepository
{
    // Run after completed TLS observation. A retained ID is the durable retry source;
    // an unobserved staged key must not be reported as promoted by a request alone.
    public RoutineRotationPromotionReceipt? ReadPendingPeerRotationReceipt(Guid peer, string actualPeerFingerprint, string actualLocalFingerprint)
    {
        Id(peer); Fingerprint(actualPeerFingerprint);
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true);
        if (RequireHost(c, tx) != actualLocalFingerprint) throw RotationRefused();
        var trust = RequireObservedActivePeer(c, tx, peer, actualPeerFingerprint);
        if (trust.CurrentFingerprint != actualPeerFingerprint) throw RotationRefused();
        if (trust.PendingFingerprint is not null) throw new InvalidOperationException("The new peer credential has not been observed.");
        return trust.PendingRotationId is { } rotation ? new(Guid.NewGuid(), peer, rotation, actualPeerFingerprint) : null;
    }
}
