using System.Security.Authentication;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

internal enum PeerTrafficPurpose { PairingFinalization = 1, OrdinaryManagement = 2 }
internal sealed record AuthenticatedPeerTransport(Guid PeerHostId, string PresentedFingerprint, string TrustState, bool UsesPendingCredential,
    bool PromotedCredential = false);

// Call on every RPC with the completed TLS handshake's actual public fingerprint. This is
// identity/traffic classification and observed rotation completion. #45 must authorize each operation
// and recheck current trust inside that authoritative transaction; this object is not a grant.
internal sealed class PeerTransportAuthentication(PeerTrustRepository repository, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    // TLS certificate validation runs BEFORE possession proof completes: it must never mutate trust.
    internal bool AdmitHandshake(Guid peer, string tlsFingerprint, PeerTrafficPurpose purpose)
    { ReadAllowed(peer, tlsFingerprint, purpose); return true; }
    private PeerTrustRecord ReadAllowed(Guid peer, string tlsFingerprint, PeerTrafficPurpose purpose)
    {
        if (peer == Guid.Empty || !HostTrustPlanning.Fingerprint(tlsFingerprint) ||
            purpose is not (PeerTrafficPurpose.PairingFinalization or PeerTrafficPurpose.OrdinaryManagement)) throw Refused();
        var trust = repository.Read(peer);
        if (trust is null || trust.RecoveryRequired || trust.State is not ("PeerBound" or "Active")) throw Refused();
        var current = trust.CurrentFingerprint == tlsFingerprint;
        var pending = trust.PendingFingerprint == tlsFingerprint;
        if (!current && !pending) throw Refused();
        if (trust.State == "PeerBound" && (purpose != PeerTrafficPurpose.PairingFinalization ||
            trust.LocalBoundFingerprint is null || trust.ExpiresUtc is null || trust.ExpiresUtc <= time.GetUtcNow())) throw Refused();
        return trust;
    }
    internal AuthenticatedPeerTransport Authenticate(Guid peer, string tlsFingerprint, PeerTrafficPurpose purpose)
    {
        var trust = ReadAllowed(peer, tlsFingerprint, purpose);
        if (trust.State == "Active")
        {
            // Fresh transaction rechecks this actual connection against current trust. New
            // presentation promotes; an Old-authenticated status claim can never do so.
            var observation = repository.ObserveActivePeerCredential(peer, tlsFingerprint);
            return new(peer, tlsFingerprint, observation.Trust.State, false, observation.Promoted);
        }
        return new(peer, tlsFingerprint, trust.State, trust.CurrentFingerprint != tlsFingerprint && trust.PendingFingerprint == tlsFingerprint);
    }
    private static AuthenticationException Refused() => new("Peer transport authentication refused.");
}
