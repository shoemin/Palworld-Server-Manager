using System.Security.Authentication;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

internal enum PeerTrafficPurpose { PairingFinalization = 1, OrdinaryManagement = 2 }
internal sealed record AuthenticatedPeerTransport(Guid PeerHostId, string PresentedFingerprint, string TrustState, bool UsesPendingCredential);

// Call on every RPC with the completed TLS handshake's actual public fingerprint. This is
// identity/traffic classification only. #45 must separately authorize each requested operation
// and recheck current trust inside that authoritative transaction; this object is not a grant.
internal sealed class PeerTransportAuthentication(PeerTrustRepository repository, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    internal AuthenticatedPeerTransport Authenticate(Guid peer, string tlsFingerprint, PeerTrafficPurpose purpose)
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
        // A recorded pending rotation pin remains recognized live even after its staging
        // deadline; PendingReconfirmationRequired gates refresh, not TLS identity (§4a-i).
        return new(peer, tlsFingerprint, trust.State, !current && pending);
    }
    private static AuthenticationException Refused() => new("Peer transport authentication refused.");
}
