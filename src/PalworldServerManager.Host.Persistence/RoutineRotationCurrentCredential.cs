using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

// Confirmation of current trust, not a claim that this peer historically promoted a key.
public sealed record RoutineRotationCredentialConfirmation(Guid RequestId, Guid HostId, Guid RotationId, string NewFingerprint);

public sealed partial class HostCredentialStateRepository
{
    public RoutineRotationCredentialConfirmation PrepareCurrentCredentialConfirmation(Guid rotationId, string actualLocalFingerprint)
    {
        if (rotationId == Guid.Empty || !HostTrustPlanning.Fingerprint(actualLocalFingerprint)) throw RoutineDenied();
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true);
        RequireCurrentRotation(c, tx, rotationId, actualLocalFingerprint);
        return new(Guid.NewGuid(), _hostId, rotationId, actualLocalFingerprint);
    }
    // Only the trusted Host adapter calls this after a matching explicit reply on pinned TLS.
    public bool RecordCurrentCredentialConfirmation(RoutineRotationCredentialConfirmation proof, Guid peer,
        string actualPeerFingerprint, string actualLocalFingerprint, long negotiatedIncarnation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proof); ct.ThrowIfCancellationRequested();
        if (proof.RequestId == Guid.Empty || proof.HostId != _hostId || proof.RotationId == Guid.Empty ||
            peer == Guid.Empty || peer == _hostId || negotiatedIncarnation <= 0 || !HostTrustPlanning.Fingerprint(proof.NewFingerprint) ||
            !HostTrustPlanning.Fingerprint(actualPeerFingerprint) || proof.NewFingerprint != actualLocalFingerprint) throw RoutineDenied();
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        RequireRotationEvidencePeer(c, tx, proof.RotationId, peer, actualPeerFingerprint, actualLocalFingerprint, negotiatedIncarnation);
        ct.ThrowIfCancellationRequested();
        using (var command = Command(c, tx, "SELECT Incarnation FROM HostRotationCurrentCredentialEvidence WHERE RotationId=$rotation AND PeerHostId=$peer;",
            ("$rotation", proof.RotationId.ToString("D")), ("$peer", peer.ToString("D"))))
            if (command.ExecuteScalar() is long prior && prior == negotiatedIncarnation) return false;
        var now = DateTimeOffset.UtcNow.ToString("O");
        Execute(c, tx, """
            INSERT INTO HostCredentialRotationPeers (RotationId,PeerHostId) VALUES ($rotation,$peer) ON CONFLICT(RotationId,PeerHostId) DO NOTHING;
            INSERT INTO HostRotationCurrentCredentialEvidence (RotationId,PeerHostId,Incarnation,ConfirmedUtc) VALUES ($rotation,$peer,$incarnation,$now)
                ON CONFLICT(RotationId,PeerHostId) DO UPDATE SET Incarnation=$incarnation,ConfirmedUtc=$now;
            INSERT INTO AuditEvents (AuditEventId,OccurredUtc,EventKind,ActorKind,ActorPeerHostId,AffectedHostId,Summary)
                VALUES ($event,$now,'HostRotationCurrentCredentialConfirmed','RemoteManager',$peer,$host,$summary);
            """, ("$rotation", proof.RotationId.ToString("D")), ("$peer", peer.ToString("D")), ("$incarnation", negotiatedIncarnation),
            ("$now", now), ("$event", Guid.NewGuid().ToString("D")), ("$host", _hostId.ToString("D")),
            ("$summary", $"Peer confirmed current credential for rotation {proof.RotationId:D}."));
        RequireRotationEvidencePeer(c, tx, proof.RotationId, peer, actualPeerFingerprint, actualLocalFingerprint, negotiatedIncarnation);
        ct.ThrowIfCancellationRequested(); tx.Commit(); return true;
    }
}

public sealed partial class PeerTrustRepository
{
    public void ConfirmObservedCurrentCredential(RoutineRotationCredentialConfirmation request, Guid peer,
        string actualPeerFingerprint, string actualLocalFingerprint, long negotiatedIncarnation)
    {
        ArgumentNullException.ThrowIfNull(request); Id(peer); Fingerprint(actualPeerFingerprint); Fingerprint(actualLocalFingerprint);
        if (request.RequestId == Guid.Empty || request.RotationId == Guid.Empty || request.HostId != peer ||
            request.NewFingerprint != actualPeerFingerprint || negotiatedIncarnation <= 0) throw RotationRefused();
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true);
        if (RequireHost(c, tx) != actualLocalFingerprint) throw RotationRefused();
        var trust = RequireObservedActivePeer(c, tx, peer, actualPeerFingerprint);
        if (trust.CurrentFingerprint != actualPeerFingerprint || PeerRelationshipIncarnation.Read(c, tx, peer) != negotiatedIncarnation)
            throw RotationRefused();
        // Sending a reply cannot prove that the other Host committed. Leave any retained receipt ID intact.
    }
}
