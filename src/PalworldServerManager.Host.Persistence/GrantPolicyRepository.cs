using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed class StaleAuthorizationRevisionException():Exception("Permission policy changed; refresh before retrying.");
public sealed record GrantMutationResult(Guid GrantId,long Revision,int ChangedGrants);

public sealed partial class GrantPolicyRepository
{
    private void RequireLocal(SqliteConnection c,SqliteTransaction tx,LocalPrincipalMutationActor actor,AuthorizationSnapshot snapshot)
    {
        if(actor.HostId!=hostId||actor.LocalPrincipalId==Guid.Empty||string.IsNullOrWhiteSpace(actor.OsPrincipalRef)||
            actor.OsPrincipalRef.Length>256||string.IsNullOrEmpty(actor.PublicVerificationKey)||
            !snapshot.Policy.IsActive(ActorRef.LocalPrincipal(actor.LocalPrincipalId)))throw new AuthenticationException("Current local identity required.");
        using var cmd=Command(c,tx,"""
            SELECT COUNT(*) FROM LocalPrincipals WHERE LocalPrincipalId=$id AND State='Active'
                AND OsPrincipalRef=$native AND PublicVerificationKey=$key;
            """,("$id",Id(actor.LocalPrincipalId)),("$native",actor.OsPrincipalRef),("$key",actor.PublicVerificationKey));
        if(Convert.ToInt32(cmd.ExecuteScalar())!=1)throw new AuthenticationException("Current local identity required.");
    }
    private static void RequireRevision(long expected,long actual)
    {if(expected<0||expected!=actual)throw new StaleAuthorizationRevisionException();}
    // actor comes from the authenticated local channel, never a deserialized request field.
    public GrantMutationResult IssueHost(LocalPrincipalMutationActor actor,long expectedRevision,Guid grantId,ActorRef grantee,
        HostCapability capability,Guid targetHostId,DelegationRights rights,Guid? sourceGrantId,CancellationToken ct=default)
        =>Issue(LocalWriter(actor),expectedRevision,new HostGrantRequest(grantId,grantee,capability,targetHostId,rights,sourceGrantId),ct);
    public GrantMutationResult IssueServer(LocalPrincipalMutationActor actor,long expectedRevision,Guid grantId,ActorRef grantee,
        ServerCapability capability,ServerRef target,DelegationRights rights,Guid? sourceGrantId,CancellationToken ct=default)
        =>Issue(LocalWriter(actor),expectedRevision,new ServerGrantRequest(grantId,grantee,capability,target,rights,sourceGrantId),ct);
    private GrantMutationResult Issue(GrantWriter actor,long expectedRevision,GrantRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        var before=Read(c,tx);actor.Require(c,tx,before);RequireRevision(expectedRevision,before.Revision);
        var now=time.GetUtcNow();
        var grant=AuthorizeOrAudit<CapabilityGrant>(c,tx,actor,before.Revision,GrantAttempt(request),()=>
        {
            if(actor.Actual.Kind==ActorKind.RemoteManager)
                RequireIncomingTarget(request is HostGrantRequest h?h.TargetHostId:((ServerGrantRequest)request).Target.AuthoritativeHostId);
            return request switch
            {
                HostGrantRequest h=>before.Policy.IssueHost(actor.Actual,h.GrantId,h.Grantee,h.Capability,h.TargetHostId,h.Rights,h.SourceGrantId,now),
                ServerGrantRequest s=>before.Policy.IssueServer(actor.Actual,s.GrantId,s.Grantee,s.Capability,s.Target,s.Rights,s.SourceGrantId,now),
                _=>throw new ArgumentException("Unknown grant request.")
            };
        },ct);
        Insert(c,tx,grant);Audit(c,tx,actor.Actual,grant,"CapabilityGrantIssued",now,1,"Direct");
        var after=Read(c,tx);actor.Require(c,tx,after);RequireRevision(checked(before.Revision+1),after.Revision);
        CapabilityGrant persisted=grant is HostCapabilityGrant?after.HostGrants.Single(g=>g.GrantId==grant.GrantId):after.ServerGrants.Single(g=>g.GrantId==grant.GrantId);
        if(persisted!=grant)throw new UnauthorizedAccessException("Grant effect changed before commit.");
        ct.ThrowIfCancellationRequested();tx.Commit();return new(grant.GrantId,after.Revision,1);
    }
    private static void Insert(SqliteConnection c,SqliteTransaction tx,CapabilityGrant grant)
    {
        var host=grant as HostCapabilityGrant;var server=grant as ServerCapabilityGrant;
        var table=host is not null?"HostCapabilityGrants":"ServerCapabilityGrants";
        var columns=host is not null?"TargetHostId":"AuthoritativeHostId,ServerProfileId";
        var targets=host is not null?"$host":"$host,$server";
        Execute(c,tx,$"""
            INSERT INTO {table} (GrantId,Capability,{columns},GranteeActorKind,GranteeLocalPrincipalId,GranteePeerHostId,
                GrantedByActorKind,GrantedByLocalPrincipalId,GrantedByPeerHostId,CanDelegate,CanDelegateOnwardDelegation,DerivedFromGrantId,CreatedUtc)
            VALUES ($id,$cap,{targets},$gkind,$glocal,$gpeer,$akind,$alocal,$apeer,$delegate,$onward,$source,$now);
            """,("$id",Id(grant.GrantId)),("$cap",host?.Capability.ToString()??server!.Capability.ToString()),
            ("$host",Id(host?.TargetHostId??server!.Target.AuthoritativeHostId)),("$server",server?.Target.ServerProfileId.ToString("D")),
            ("$gkind",grant.GranteeActor.Kind.ToString()),("$glocal",grant.GranteeActor.Kind==ActorKind.LocalPrincipal?Id(grant.GranteeActor.Id):null),
            ("$gpeer",grant.GranteeActor.Kind==ActorKind.RemoteManager?Id(grant.GranteeActor.Id):null),
            ("$akind",grant.GrantedByActor.Kind.ToString()),("$alocal",grant.GrantedByActor.Kind==ActorKind.LocalPrincipal?Id(grant.GrantedByActor.Id):null),
            ("$apeer",grant.GrantedByActor.Kind==ActorKind.RemoteManager?Id(grant.GrantedByActor.Id):null),
            ("$delegate",grant.Rights.CanDelegate?1:0),("$onward",grant.Rights.CanDelegateOnwardDelegation?1:0),
            ("$source",grant.DerivedFromGrantId?.ToString("D")),("$now",Stamp(grant.GrantedUtc)));
    }
    private static void Audit(SqliteConnection c,SqliteTransaction tx,LocalPrincipalMutationActor actor,CapabilityGrant grant,
        string kind,DateTimeOffset now,int changed=1)
        =>Audit(c,tx,ActorRef.LocalPrincipal(actor.LocalPrincipalId),grant,kind,now,changed,"Direct");
    private static void Audit(SqliteConnection c,SqliteTransaction tx,ActorRef actor,CapabilityGrant grant,
        string kind,DateTimeOffset now,int changed,string origin)
    {
        var host=grant as HostCapabilityGrant;var server=grant as ServerCapabilityGrant;
        var summary=$"Grant={Id(grant.GrantId)}; Source={grant.DerivedFromGrantId?.ToString("D")??"OwnerRoot"}; Grantee={grant.GranteeActor.Kind}:{Id(grant.GranteeActor.Id)}; Capability={host?.Capability.ToString()??server!.Capability.ToString()}; Delegate={grant.Rights.CanDelegate}; Onward={grant.Rights.CanDelegateOnwardDelegation}; Changed={changed}; Issuer={grant.GrantedByActor.Kind}:{Id(grant.GrantedByActor.Id)}; Origin={origin}.";
        Execute(c,tx,"""
            INSERT INTO AuditEvents (AuditEventId,OccurredUtc,EventKind,ActorKind,ActorLocalPrincipalId,ActorPeerHostId,
                AffectedHostId,AffectedServerProfileId,IsOfflineRecovery,Summary)
            VALUES ($id,$now,$kind,$actorKind,$actor,$peer,$host,$server,0,$summary);
            """,("$id",Id(Guid.NewGuid())),("$now",Stamp(now)),("$kind",kind),("$actorKind",actor.Kind.ToString()),
            ("$actor",actor.Kind==ActorKind.LocalPrincipal?Id(actor.Id):null),("$peer",actor.Kind==ActorKind.RemoteManager?Id(actor.Id):null),
            ("$host",Id(host?.TargetHostId??server!.Target.AuthoritativeHostId)),("$server",server?.Target.ServerProfileId.ToString("D")),("$summary",summary));
    }
    // This bounded primitive requires structural Owner. It grants no new revocation semantics
    // to ManagePermissions or to a remote caller.
    public GrantMutationResult InvalidateHostSubtree(LocalPrincipalMutationActor owner,long expectedRevision,Guid root,CancellationToken ct=default)
        =>Invalidate(owner,expectedRevision,root,false,ct);
    public GrantMutationResult InvalidateServerSubtree(LocalPrincipalMutationActor owner,long expectedRevision,Guid root,CancellationToken ct=default)
        =>Invalidate(owner,expectedRevision,root,true,ct);
    private GrantMutationResult Invalidate(LocalPrincipalMutationActor owner,long expectedRevision,Guid root,bool server,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(owner);Id(root);ct.ThrowIfCancellationRequested();
        using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        var before=Read(c,tx);RequireLocal(c,tx,owner,before);RequireRevision(expectedRevision,before.Revision);
        var grants=server?before.ServerGrants.Cast<CapabilityGrant>().ToArray():before.HostGrants.Cast<CapabilityGrant>().ToArray();
        var rootGrant=AuthorizeOrAudit(c,tx,LocalWriter(owner),before.Revision,
            new(server?"InvalidateServerSubtree":"InvalidateHostSubtree",hostId,null,"Root="+Id(root)),()=>
        {
            if(!before.Policy.IsOwner(ActorRef.LocalPrincipal(owner.LocalPrincipalId)))throw new UnauthorizedAccessException("Owner authorization required.");
            return grants.SingleOrDefault(g=>g.GrantId==root)??throw new UnauthorizedAccessException("Grant subtree unavailable.");
        },ct);
        var ids=(server?before.Policy.ServerSubtree(root):before.Policy.HostSubtree(root)).ToHashSet();
        var changed=grants.Where(g=>ids.Contains(g.GrantId)&&g.InvalidatedUtc is null).ToArray();
        if(changed.Length==0)return new(root,before.Revision,0);
        var now=time.GetUtcNow();var table=server?"ServerCapabilityGrants":"HostCapabilityGrants";
        foreach(var grant in changed)Execute(c,tx,$"UPDATE {table} SET InvalidatedUtc=$now WHERE GrantId=$id AND InvalidatedUtc IS NULL;",("$now",Stamp(now)),("$id",Id(grant.GrantId)));
        Audit(c,tx,owner,rootGrant,"CapabilityGrantSubtreeInvalidated",now,changed.Length);
        var after=Read(c,tx);RequireLocal(c,tx,owner,after);RequireRevision(checked(before.Revision+changed.Length),after.Revision);
        if(!after.Policy.IsOwner(ActorRef.LocalPrincipal(owner.LocalPrincipalId)))throw new UnauthorizedAccessException("Owner authorization changed.");
        var actual=(server?after.ServerGrants.Cast<CapabilityGrant>():after.HostGrants.Cast<CapabilityGrant>()).ToDictionary(g=>g.GrantId);
        foreach(var grant in changed)if(actual[grant.GrantId].InvalidatedUtc!=now)throw new UnauthorizedAccessException("Grant invalidation changed before commit.");
        ct.ThrowIfCancellationRequested();tx.Commit();return new(root,after.Revision,changed.Length);
    }
}
