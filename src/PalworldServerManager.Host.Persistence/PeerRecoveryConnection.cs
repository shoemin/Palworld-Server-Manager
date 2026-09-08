using System.Security.Authentication;

namespace PalworldServerManager.Host.Persistence;

public sealed partial class PeerTrustRepository
{
    // Additional certificate admission only; completed TLS and fixed recovery negotiation
    // must still bind the claimed Host to the actual keys and relationship incarnation.
    public bool RecognizesActiveRecoveryFingerprint(string fingerprint)
    {
        Fingerprint(fingerprint); using var c=Open(); using var tx=c.BeginTransaction(deferred:true); RequireHost(c,tx);
        using var command=Command(c,tx,"""
            SELECT 1 FROM TrustedManagers WHERE State='Active' AND PeerRecoveryRequired=1
            AND (CurrentTrustedPublicKeyFingerprint=$fp OR PendingTrustedPublicKeyFingerprint=$fp) LIMIT 1;
            """,("$fp",fingerprint));
        return command.ExecuteScalar() is not null;
    }

    // Read-only proof for the fixed receipt surface. Never observes/promotes a staged
    // key, clears recovery, approves a replacement or authorizes ordinary peer traffic.
    public long ReadRecoveryRelationshipIncarnation(Guid peer,string actualPeerFingerprint,string actualLocalFingerprint,
        CancellationToken ct=default)
    {
        Id(peer); Fingerprint(actualPeerFingerprint); Fingerprint(actualLocalFingerprint);
        ct.ThrowIfCancellationRequested(); using var c=Open(); using var tx=c.BeginTransaction(deferred:true);
        if(peer==hostId||RequireHost(c,tx)!=actualLocalFingerprint)throw new AuthenticationException("Recovery connection refused.");
        var trust=Read(c,tx,peer);
        if(trust is not {State:"Active"} || (trust.CurrentFingerprint!=actualPeerFingerprint&&trust.PendingFingerprint!=actualPeerFingerprint))
            throw new AuthenticationException("Recovery connection refused.");
        ValidateRotationMetadata(trust);
        var incarnation=PeerRelationshipIncarnation.Read(c,tx,peer);
        ct.ThrowIfCancellationRequested(); return incarnation;
    }
}
