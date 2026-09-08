using System.Security.Authentication;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

public sealed partial class PeerSecurityRpcRuntime
{
    internal PeerRecoveryContactCoordinator? RecoveryContacts {get;private set;}
    internal void ConfigureRecoveryContacts(PeerRecoveryContactCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        if(coordinator.HostId!=HostId)throw new ArgumentException("Recovery coordinator belongs to another Host.");
        if(RecoveryContacts is not null)throw new InvalidOperationException("Recovery contact coordinator already configured.");
        RecoveryContacts=coordinator;
    }
    // Call only after actual native TLS and semantic Host/protocol negotiation. Never from
    // an incoming address claim, pairing port, recovery RPC or denied ordinary operation.
    internal Task<PeerRecoveryContactOutcome> AuthenticatedRecoveryContact(Guid peer,Uri address,PeerTlsConnectionIdentity actual,CancellationToken ct)
    {
        if(RecoveryContacts is null)return Task.FromResult(PeerRecoveryContactOutcome.NotScheduled);
        ct.ThrowIfCancellationRequested();
        if(Repository.Read(peer) is not {State:"Active"})return Task.FromResult(PeerRecoveryContactOutcome.NotScheduled);
        var contact=new PeerRecoveryContact(peer,address,actual);RequireRecoveryContact(contact,ct);
        return RecoveryContacts.Notify(contact);
    }
    internal void RequireRecoveryContact(PeerRecoveryContact contact,CancellationToken ct)
    {
        Repository.ReadRecoveryRelationshipIncarnation(contact.Peer,contact.Identity.PeerFingerprint,contact.Identity.LocalFingerprint,ct);
        if(Repository.Read(contact.Peer)?.CurrentFingerprint!=contact.Identity.PeerFingerprint)
            throw new AuthenticationException("Current recovery contact key required.");
    }
}
