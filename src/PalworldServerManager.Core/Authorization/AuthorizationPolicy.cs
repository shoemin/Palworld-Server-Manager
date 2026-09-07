namespace PalworldServerManager.Core.Authorization;

// Immutable, trusted facts from ONE authoritative Host snapshot. This is not authentication
// and is never built from a request's claims. Persistence must rebuild it inside its writer
// transaction before issuance/invalidation; a prior snapshot cannot authorize a later write.
public sealed class AuthorizationPolicy
{
    private readonly ActorRef? owner;
    private readonly HashSet<ActorRef> active;
    private readonly Dictionary<Guid,HostCapabilityGrant> hosts;
    private readonly Dictionary<Guid,ServerCapabilityGrant> servers;
    public Guid ThisHostId { get; }
    public bool Initialized=>owner is not null;
    public AuthorizationPolicy(Guid thisHostId,Guid? ownerLocalPrincipalId,IEnumerable<Guid> activeLocalPrincipals,
        IEnumerable<Guid> activePeerHosts,IEnumerable<HostCapabilityGrant> hostGrants,IEnumerable<ServerCapabilityGrant> serverGrants)
    {
        if(thisHostId==Guid.Empty)throw new ArgumentException("Host identity is required.");ThisHostId=thisHostId;
        ArgumentNullException.ThrowIfNull(activeLocalPrincipals);ArgumentNullException.ThrowIfNull(activePeerHosts);
        ArgumentNullException.ThrowIfNull(hostGrants);ArgumentNullException.ThrowIfNull(serverGrants);
        owner=ownerLocalPrincipalId is {} id?ActorRef.LocalPrincipal(id):null;
        active=activeLocalPrincipals.Select(ActorRef.LocalPrincipal).Concat(activePeerHosts.Select(ActorRef.RemoteManager)).ToHashSet();
        if(owner is not null && !active.Contains(owner))throw new ArgumentException("Initialized Owner must be active.");
        if(active.Contains(ActorRef.RemoteManager(thisHostId)))throw new ArgumentException("Host cannot be its own peer.");
        hosts=hostGrants.ToDictionary(g=>g.GrantId);servers=serverGrants.ToDictionary(g=>g.GrantId);
    }
    public bool IsActive(ActorRef? actor)=>Initialized && actor is not null && active.Contains(actor);
    public bool IsOwner(ActorRef? actor)=>actor is not null && actor==owner && IsActive(actor);
    private static bool Derives(CapabilityGrant child,CapabilityGrant parent)
        =>child.GrantedByActor==parent.GranteeActor && parent.Rights.CanDelegate &&
            ((!child.Rights.CanDelegate && !child.Rights.CanDelegateOnwardDelegation) || parent.Rights.CanDelegateOnwardDelegation) &&
            (child,parent) switch
            {
                (HostCapabilityGrant c,HostCapabilityGrant p)=>c.Capability==p.Capability && c.TargetHostId==p.TargetHostId,
                (ServerCapabilityGrant c,ServerCapabilityGrant p)=>c.Capability==p.Capability && c.Target==p.Target,
                _=>false
            };
    private bool Valid<T>(T grant,Dictionary<Guid,T> forest) where T:CapabilityGrant
    {
        var seen=new HashSet<Guid>();CapabilityGrant current=grant;
        while(true)
        {
            if(!seen.Add(current.GrantId) || current.InvalidatedUtc is not null ||
                !IsActive(current.GranteeActor) || !IsActive(current.GrantedByActor))return false;
            if(current.DerivedFromGrantId is not {} parent)return IsOwner(current.GrantedByActor);
            if(!forest.TryGetValue(parent,out var source) || !Derives(current,source))return false;
            current=source;
        }
    }
    public bool CanUseHost(ActorRef? actor,HostCapability capability,Guid targetHostId)
        =>Enum.IsDefined(capability) && targetHostId!=Guid.Empty && IsActive(actor) &&
            (IsOwner(actor) || hosts.Values.Any(g=>g.GranteeActor==actor && g.Capability==capability && g.TargetHostId==targetHostId && Valid(g,hosts)));
    public bool CanUseServer(ActorRef? actor,ServerCapability capability,ServerRef? target)
        =>Enum.IsDefined(capability) && target is not null && IsActive(actor) &&
            (IsOwner(actor) || servers.Values.Any(g=>g.GranteeActor==actor && g.Capability==capability && g.Target==target && Valid(g,servers)));
    private bool MayIssue<T>(ActorRef issuer,T candidate,Dictionary<Guid,T> forest) where T:CapabilityGrant
    {
        if(!IsActive(issuer) || !IsActive(candidate.GranteeActor) || forest.ContainsKey(candidate.GrantId))return false;
        if(candidate.DerivedFromGrantId is not {} id)return IsOwner(issuer);
        return forest.TryGetValue(id,out var parent) && Valid(parent,forest) && Derives(candidate,parent);
    }
    public HostCapabilityGrant IssueHost(ActorRef issuer,Guid id,ActorRef grantee,HostCapability capability,Guid targetHostId,
        DelegationRights rights,Guid? sourceGrantId,DateTimeOffset utc)
    {
        var grant=new HostCapabilityGrant(id,grantee,capability,targetHostId,rights,issuer,sourceGrantId,utc);
        if(!MayIssue(issuer,grant,hosts))throw new UnauthorizedAccessException("Grant issuance refused.");return grant;
    }
    public ServerCapabilityGrant IssueServer(ActorRef issuer,Guid id,ActorRef grantee,ServerCapability capability,ServerRef target,
        DelegationRights rights,Guid? sourceGrantId,DateTimeOffset utc)
    {
        var grant=new ServerCapabilityGrant(id,grantee,capability,target,rights,issuer,sourceGrantId,utc);
        if(!MayIssue(issuer,grant,servers))throw new UnauthorizedAccessException("Grant issuance refused.");return grant;
    }
    public PresetExpansion ExpandPreset(ActorRef issuer,RolePreset preset,DateTimeOffset utc)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if(!IsActive(issuer))throw new UnauthorizedAccessException("Active preset applier required.");
        // Owner entries are roots by contract. A non-Owner must use an exact source already
        // held in THIS snapshot; no new candidate can bootstrap another entry's authority.
        var root=IsOwner(issuer);
        var hs=preset.Hosts.Select(p=>IssueHost(issuer,p.GrantId,p.Grantee,p.Capability,p.TargetHostId,p.Rights,root?null:p.SourceGrantId,utc)).ToArray();
        var ss=preset.Servers.Select(p=>IssueServer(issuer,p.GrantId,p.Grantee,p.Capability,p.Target,p.Rights,root?null:p.SourceGrantId,utc)).ToArray();
        return new(Array.AsReadOnly(hs),Array.AsReadOnly(ss));
    }
    // Precise per-type effect plans, not writes. The later persistence unit must apply each
    // plan atomically with its audit/revision under a fresh policy transaction.
    private static IReadOnlyList<Guid> Subtree<T>(Guid root,Dictionary<Guid,T> forest) where T:CapabilityGrant
    {
        if(!forest.ContainsKey(root))return Array.Empty<Guid>();
        var children=forest.Values.Where(g=>g.DerivedFromGrantId is not null).ToLookup(g=>g.DerivedFromGrantId!.Value);
        var result=new HashSet<Guid>();var pending=new Queue<Guid>();pending.Enqueue(root);
        while(pending.TryDequeue(out var id))if(result.Add(id))foreach(var child in children[id])pending.Enqueue(child.GrantId);
        return Array.AsReadOnly(result.Order().ToArray());
    }
    public IReadOnlyList<Guid> HostSubtree(Guid root)=>Subtree(root,hosts);
    public IReadOnlyList<Guid> ServerSubtree(Guid root)=>Subtree(root,servers);
}

public static class RemoteAuthorization
{
    private static bool LocalPath(AuthorizationPolicy local,ActorRef actor,AuthorizationPolicy remote)
        =>actor.Kind==ActorKind.LocalPrincipal && local.ThisHostId!=remote.ThisHostId &&
            local.IsActive(actor) && local.IsActive(ActorRef.RemoteManager(remote.ThisHostId));
    public static bool CanUseHost(AuthorizationPolicy local,ActorRef actor,AuthorizationPolicy remote,HostCapability capability)
    {
        ArgumentNullException.ThrowIfNull(local);ArgumentNullException.ThrowIfNull(actor);ArgumentNullException.ThrowIfNull(remote);
        return LocalPath(local,actor,remote) && local.CanUseHost(actor,capability,remote.ThisHostId) &&
            remote.CanUseHost(ActorRef.RemoteManager(local.ThisHostId),capability,remote.ThisHostId);
    }
    public static bool CanUseServer(AuthorizationPolicy local,ActorRef actor,AuthorizationPolicy remote,ServerCapability capability,ServerRef target)
    {
        ArgumentNullException.ThrowIfNull(local);ArgumentNullException.ThrowIfNull(actor);ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(target);
        return target.AuthoritativeHostId==remote.ThisHostId && LocalPath(local,actor,remote) && local.CanUseServer(actor,capability,target) &&
            remote.CanUseServer(ActorRef.RemoteManager(local.ThisHostId),capability,target);
    }
}
