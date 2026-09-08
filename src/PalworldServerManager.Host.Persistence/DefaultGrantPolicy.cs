using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed record DefaultGrantTemplateSnapshot(long Revision,Guid? ConfigurationId,Guid? ConfiguredByLocalPrincipalId,
    DateTimeOffset? ConfiguredUtc,DefaultGrantTemplate Template)
{
    public bool IsConfigured=>ConfigurationId is not null;
}

public sealed partial class GrantPolicyRepository
{
    public DefaultGrantTemplateSnapshot ReadDefaults()
    {using var c=Open(true);using var tx=c.BeginTransaction(deferred:true);return ReadDefaults(c,tx,Read(c,tx).Revision);}
    private DefaultGrantTemplateSnapshot ReadDefaults(SqliteConnection c,SqliteTransaction tx,long revision)
    {
        Guid? config,by;DateTimeOffset? utc;
        using(var cmd=Command(c,tx,"SELECT ConfigurationId,ConfiguredByLocalPrincipalId,ConfiguredUtc FROM DefaultGrantTemplateState WHERE Id=1;"))
        {
            using var r=cmd.ExecuteReader();if(!r.Read())throw Corrupt();
            config=r.IsDBNull(0)?null:ParseId(r.GetString(0));by=r.IsDBNull(1)?null:ParseId(r.GetString(1));utc=r.IsDBNull(2)?null:ParseTime(r.GetString(2));
            if((config is null)!=(by is null)||(config is null)!=(utc is null)||(utc is {} stamp&&stamp.Offset!=TimeSpan.Zero))throw Corrupt();
        }
        var hs=new List<HostDefaultGrant>();var ss=new List<ServerDefaultGrant>();
        using(var cmd=Command(c,tx,"SELECT Capability,CanDelegate,CanDelegateOnwardDelegation FROM HostDefaultGrants;"))
        {using var r=cmd.ExecuteReader();while(r.Read())hs.Add(new(Capability<HostCapability>(r.GetString(0)),new(r.GetInt32(1)==1,r.GetInt32(2)==1)));}
        using(var cmd=Command(c,tx,"SELECT Capability,AuthoritativeHostId,ServerProfileId,CanDelegate,CanDelegateOnwardDelegation FROM ServerDefaultGrants;"))
        {using var r=cmd.ExecuteReader();while(r.Read())ss.Add(new(Capability<ServerCapability>(r.GetString(0)),new(ParseId(r.GetString(1)),ParseId(r.GetString(2))),new(r.GetInt32(3)==1,r.GetInt32(4)==1)));}
        var template=new DefaultGrantTemplate(hs,ss);ValidateDefaultTargets(template);
        if(config is null&&(hs.Count!=0||ss.Count!=0))throw Corrupt();
        return new(revision,config,by,utc,template);
    }
    private void ValidateDefaultTargets(DefaultGrantTemplate template)
    {if(template.Servers.Any(s=>s.Target.AuthoritativeHostId!=hostId))throw new ArgumentException("Defaults must target this authoritative Host.");}
    private static bool SameTemplate(DefaultGrantTemplate a,DefaultGrantTemplate b)
        =>a.Hosts.Count==b.Hosts.Count&&a.Servers.Count==b.Servers.Count&&a.Hosts.ToHashSet().SetEquals(b.Hosts)&&a.Servers.ToHashSet().SetEquals(b.Servers);
    public DefaultGrantTemplateSnapshot ConfigureDefaults(LocalPrincipalMutationActor actor,long expectedRevision,DefaultGrantTemplate template,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(actor);ArgumentNullException.ThrowIfNull(template);ValidateDefaultTargets(template);ct.ThrowIfCancellationRequested();
        using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        var before=Read(c,tx);RequireLocal(c,tx,actor,before);RequireRevision(expectedRevision,before.Revision);
        AuthorizeOrAudit(c,tx,LocalWriter(actor),before.Revision,
            new("ConfigureDefaults",hostId,null,$"HostEntries={template.Hosts.Count}; ServerEntries={template.Servers.Count}"),()=>
        {
            if(!before.Policy.IsOwner(ActorRef.LocalPrincipal(actor.LocalPrincipalId)))throw new UnauthorizedAccessException("Only Owner may configure defaults.");
            return true;
        },ct);
        var prior=ReadDefaults(c,tx,before.Revision);var config=Guid.NewGuid();var now=time.GetUtcNow();
        Execute(c,tx,"DELETE FROM HostDefaultGrants; DELETE FROM ServerDefaultGrants;");
        foreach(var entry in template.Hosts)Execute(c,tx,"INSERT INTO HostDefaultGrants VALUES ($cap,$delegate,$onward);",
            ("$cap",entry.Capability.ToString()),("$delegate",entry.Rights.CanDelegate?1:0),("$onward",entry.Rights.CanDelegateOnwardDelegation?1:0));
        foreach(var entry in template.Servers)Execute(c,tx,"INSERT INTO ServerDefaultGrants VALUES ($host,$server,$cap,$delegate,$onward);",
            ("$host",Id(entry.Target.AuthoritativeHostId)),("$server",Id(entry.Target.ServerProfileId)),("$cap",entry.Capability.ToString()),
            ("$delegate",entry.Rights.CanDelegate?1:0),("$onward",entry.Rights.CanDelegateOnwardDelegation?1:0));
        Execute(c,tx,"UPDATE DefaultGrantTemplateState SET ConfigurationId=$config,ConfiguredByLocalPrincipalId=$actor,ConfiguredUtc=$now WHERE Id=1;",
            ("$config",Id(config)),("$actor",Id(actor.LocalPrincipalId)),("$now",Stamp(now)));
        var entries=string.Join(";",template.Hosts.Select(e=>$"H:{e.Capability}:{e.Rights.CanDelegate}:{e.Rights.CanDelegateOnwardDelegation}")
            .Concat(template.Servers.Select(e=>$"S:{Id(e.Target.AuthoritativeHostId)}/{Id(e.Target.ServerProfileId)}:{e.Capability}:{e.Rights.CanDelegate}:{e.Rights.CanDelegateOnwardDelegation}")));
        var audit=WriteSuccessAudit(c,tx,ActorRef.LocalPrincipal(actor.LocalPrincipalId),hostId,null,
            "DefaultGrantTemplateConfigured",now,$"Configuration={Id(config)}; Entries={entries}.");
        var after=Read(c,tx);RequireLocal(c,tx,actor,after);
        var expected=checked(before.Revision+prior.Template.Hosts.Count+prior.Template.Servers.Count+template.Hosts.Count+template.Servers.Count+1);
        RequireRevision(expected,after.Revision);
        var result=ReadDefaults(c,tx,after.Revision);
        if(!after.Policy.IsOwner(ActorRef.LocalPrincipal(actor.LocalPrincipalId))||result.ConfigurationId!=config||result.ConfiguredByLocalPrincipalId!=actor.LocalPrincipalId||
            result.ConfiguredUtc!=now||!SameTemplate(result.Template,template))throw new UnauthorizedAccessException("Default configuration changed before commit.");
        audit();ct.ThrowIfCancellationRequested();tx.Commit();return result;
    }
    // Trusted composition gets a real provider, not a default/no-op hook. Every application
    // produces a per-call validator for its SAME enclosing transaction after the trust audit.
    public IPeerActivationHook CreateDefaultActivationHook()=>new DefaultActivationHook(this);
    private sealed class DefaultActivationHook(GrantPolicyRepository repository):IPeerActivationHook
    {
        public Action Apply(SqliteConnection c,SqliteTransaction tx,PeerActivationContext activation)
            =>repository.ApplyDefaults(c,tx,activation);
    }
    private Action ApplyDefaults(SqliteConnection c,SqliteTransaction tx,PeerActivationContext activation)
        =>ApplyDefaults(c,tx,activation,null);
    private Action ApplyDefaults(SqliteConnection c,SqliteTransaction tx,PeerActivationContext activation,ApprovedReplacementContext? replacement)
    {
        if(activation.AuthoritativeHostId!=hostId||activation.ActivatedUtc.Offset!=TimeSpan.Zero)throw new UnauthorizedAccessException("Invalid activation authority.");
        var before=Read(c,tx);var peer=ActorRef.RemoteManager(activation.PeerHostId);
        if(replacement is not null&&activation.InitiatingActor!=ActorRef.LocalPrincipal(replacement.Owner.LocalPrincipalId))
            throw new UnauthorizedAccessException("Replacement default initiator must be its actual Owner.");
        var issuance=replacement is null?before.Policy:ReplacementRecipientPolicy(c,tx,before,replacement);
        if(!issuance.IsActive(peer)||replacement is not null&&replacement.Peer!=activation.PeerHostId)
            throw new UnauthorizedAccessException("Active peer required for defaults.");
        if(activation.InitiatingActor is null||
            (activation.InitiatingActor!=peer&&!before.Policy.IsOwner(activation.InitiatingActor)))
            throw new UnauthorizedAccessException("Activation initiator is invalid.");
        Guid owner;
        using(var cmd=Command(c,tx,"SELECT LocalPrincipalId FROM LocalPrincipals WHERE State='Active' AND IsOwner=1;"))
            owner=ParseId(cmd.ExecuteScalar() as string??throw Corrupt());
        var issuer=ActorRef.LocalPrincipal(owner);var defaults=ReadDefaults(c,tx,before.Revision);var grants=new List<CapabilityGrant>();
        foreach(var entry in defaults.Template.Hosts)grants.Add(issuance.IssueHost(issuer,Guid.NewGuid(),peer,entry.Capability,hostId,entry.Rights,null,activation.ActivatedUtc));
        foreach(var entry in defaults.Template.Servers)grants.Add(issuance.IssueServer(issuer,Guid.NewGuid(),peer,entry.Capability,entry.Target,entry.Rights,null,activation.ActivatedUtc));
        var audits=new List<Action>();
        foreach(var grant in grants)
        {
            Insert(c,tx,grant);
            // Preserve the actual authenticated local Owner or remote peer initiator,
            // separately from the structural Owner root issuer and peer grantee.
            audits.Add(Audit(c,tx,activation.InitiatingActor,grant,"DefaultGrantApplied",activation.ActivatedUtc,1,$"ConfiguredDefault:{defaults.ConfigurationId?.ToString("D")??"Factory"}"));
        }
        return ()=>
        {
            var after=Read(c,tx);RequireRevision(checked(before.Revision+grants.Count),after.Revision);
            var currentIssuance=replacement is null?after.Policy:ReplacementRecipientPolicy(c,tx,after,replacement);
            var current=ReadDefaults(c,tx,after.Revision);
            if(!after.Policy.IsOwner(issuer)||!currentIssuance.IsActive(peer)||current.ConfigurationId!=defaults.ConfigurationId||
                current.ConfiguredByLocalPrincipalId!=defaults.ConfiguredByLocalPrincipalId||current.ConfiguredUtc!=defaults.ConfiguredUtc||!SameTemplate(current.Template,defaults.Template))
                throw new UnauthorizedAccessException("Activation policy changed before commit.");
            foreach(var grant in grants)
            {
                CapabilityGrant persisted=grant is HostCapabilityGrant?after.HostGrants.Single(g=>g.GrantId==grant.GrantId):after.ServerGrants.Single(g=>g.GrantId==grant.GrantId);
                if(persisted!=grant)throw new UnauthorizedAccessException("Default grant changed before commit.");
            }
            foreach(var audit in audits)audit();
        };
    }
}
