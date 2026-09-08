using System.Security.Authentication;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed partial class GrantPolicyRepository
{
    // Trusted Host result + original completed connection evidence, never a request token.
    // Read-only safety guard for notifying a peer about an already committed local effect.
    public void RequireCommittedUnpair(PeerGrantMutationActor original,PeerTrustRevocationResult revoked,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(original);ArgumentNullException.ThrowIfNull(revoked);
        if(original.HostId!=hostId||original.PeerHostId==Guid.Empty||original.PeerHostId==hostId||original.Incarnation<=0||
            !HostTrustPlanning.Fingerprint(original.PeerFingerprint)||!HostTrustPlanning.Fingerprint(original.LocalFingerprint)||
            revoked.PeerHostId!=original.PeerHostId||!revoked.Changed||revoked.PreviousIncarnation!=original.Incarnation||
            revoked.Incarnation<=revoked.PreviousIncarnation)throw new AuthenticationException("Matching committed unpair required.");
        ct.ThrowIfCancellationRequested();using var c=Open();using var tx=c.BeginTransaction(deferred:true);
        _=Read(c,tx); // Current initialized Host and single Owner remain required.
        if(RevocationCredential(c,tx).Fingerprint!=original.LocalFingerprint)throw new AuthenticationException("Current unpair Host credential required.");
        using var cmd=Command(c,tx,"""
            SELECT COUNT(*) FROM TrustedManagers t JOIN PeerRelationshipIncarnations i ON i.PeerHostId=t.PeerHostId
            WHERE t.PeerHostId=$peer AND i.Incarnation=$inc AND t.State='Revoked' AND t.RevokedUtc IS NOT NULL
                AND t.CurrentTrustedPublicKeyFingerprint IS NULL AND t.PendingTrustedPublicKeyFingerprint IS NULL
                AND t.PendingRotationId IS NULL AND t.PendingRotationExpiresUtc IS NULL
                AND t.PendingReconfirmationRequired=0 AND t.PeerRecoveryRequired=0;
            """,("$peer",Id(original.PeerHostId)),("$inc",revoked.Incarnation));
        if(Convert.ToInt32(cmd.ExecuteScalar())!=1)throw new AuthenticationException("Current unpair tombstone required.");
        ct.ThrowIfCancellationRequested();
    }
}
