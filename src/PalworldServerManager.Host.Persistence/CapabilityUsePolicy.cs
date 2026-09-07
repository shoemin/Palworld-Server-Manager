using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed partial class GrantPolicyRepository
{
    // Trusted authenticated Host evidence only, never a request-selected actor. The returned
    // revision is a current observation, NOT an execution permit. A later operation must
    // recheck identity/policy in its own authoritative mutation boundary.
    public long RequireLocalHostCapability(LocalPrincipalMutationActor actor,HostCapability capability,Guid targetHostId,CancellationToken ct=default)
        =>RequireHostCapability(LocalWriter(actor),capability,targetHostId,ct);
    public long RequireLocalServerCapability(LocalPrincipalMutationActor actor,ServerCapability capability,ServerRef target,CancellationToken ct=default)
        =>RequireServerCapability(LocalWriter(actor),capability,target,ct);
    public long RequireRemoteHostCapability(PeerGrantMutationActor actor,HostCapability capability,Guid targetHostId,CancellationToken ct=default)
        =>RequireHostCapability(PeerWriter(actor),capability,targetHostId,ct);
    public long RequireRemoteServerCapability(PeerGrantMutationActor actor,ServerCapability capability,ServerRef target,CancellationToken ct=default)
        =>RequireServerCapability(PeerWriter(actor),capability,target,ct);

    private long RequireHostCapability(GrantWriter actor,HostCapability capability,Guid targetHostId,CancellationToken ct)
    {
        if(!Enum.IsDefined(capability)||targetHostId==Guid.Empty)throw new ArgumentException("Known Host capability and exact target required.");
        return RequireCapability(actor,new("UseHostCapability",targetHostId,null,$"Capability={capability}"),
            policy=>policy.CanUseHost(actor.Actual,capability,targetHostId),ct);
    }
    private long RequireServerCapability(GrantWriter actor,ServerCapability capability,ServerRef target,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if(!Enum.IsDefined(capability))throw new ArgumentException("Known server capability required.");
        return RequireCapability(actor,new("UseServerCapability",target.AuthoritativeHostId,target.ServerProfileId,$"Capability={capability}"),
            policy=>policy.CanUseServer(actor.Actual,capability,target),ct);
    }
    private long RequireCapability(GrantWriter actor,PermissionAttempt attempt,Func<AuthorizationPolicy,bool> permits,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        var before=Read(c,tx);actor.Require(c,tx,before);
        AuthorizeOrAudit(c,tx,actor,before.Revision,attempt,()=>
        {
            // Local outbound authority is only one ceiling. An active destination is also
            // required even for Owner; the remote Host independently checks this machine.
            var targetAllowed=actor.Actual.Kind==ActorKind.RemoteManager?attempt.Host==hostId:
                attempt.Host==hostId||before.Policy.IsActive(ActorRef.RemoteManager(attempt.Host));
            if(!targetAllowed||!permits(before.Policy))throw new UnauthorizedAccessException("Capability use refused.");
            return true;
        },ct);
        var after=Read(c,tx);actor.Require(c,tx,after);RequireRevision(before.Revision,after.Revision);
        ct.ThrowIfCancellationRequested();tx.Commit();return after.Revision;
    }
}
