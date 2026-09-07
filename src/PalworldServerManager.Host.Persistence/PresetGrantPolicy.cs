using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed record PresetGrantResult(Guid? AuditBatchId,long Revision,IReadOnlyList<Guid> HostGrantIds,IReadOnlyList<Guid> ServerGrantIds);

public sealed partial class GrantPolicyRepository
{
    public PresetGrantResult ApplyPreset(LocalPrincipalMutationActor actor,long expectedRevision,RolePreset preset,CancellationToken ct=default)
        =>ApplyPreset(LocalWriter(actor),expectedRevision,preset,ct);
    private PresetGrantResult ApplyPreset(GrantWriter actor,long expectedRevision,RolePreset preset,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(preset);ct.ThrowIfCancellationRequested();
        using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        var before=Read(c,tx);actor.Require(c,tx,before);RequireRevision(expectedRevision,before.Revision);
        var now=time.GetUtcNow();var actual=actor.Actual;
        var expansion=AuthorizeOrAudit(c,tx,actor,before.Revision,
            new("ApplyPreset",hostId,null,$"HostEntries={preset.Hosts.Count}; ServerEntries={preset.Servers.Count}"),()=>
        {
            if(actual.Kind==ActorKind.RemoteManager)
            {
                foreach(var entry in preset.Hosts)RequireIncomingTarget(entry.TargetHostId);
                foreach(var entry in preset.Servers)RequireIncomingTarget(entry.Target.AuthoritativeHostId);
            }
            return before.Policy.ExpandPreset(actual,preset,now);
        },ct);
        var grants=expansion.Hosts.Cast<CapabilityGrant>().Concat(expansion.Servers).ToArray();
        ct.ThrowIfCancellationRequested();
        if(grants.Length==0)return new(null,before.Revision,Array.Empty<Guid>(),Array.Empty<Guid>());
        var batch=Guid.NewGuid();var audits=new List<Action>();
        foreach(var grant in grants)
        {
            Insert(c,tx,grant);
            // Correlation only, not a stored role or a long-running operation identity.
            // Never include the caller's free-text presentation label in audit/diagnostics.
            audits.Add(Audit(c,tx,actual,grant,"CapabilityGrantIssued",now,1,"RolePreset:"+Id(batch)));
        }
        var after=Read(c,tx);actor.Require(c,tx,after);RequireRevision(checked(before.Revision+grants.Length),after.Revision);
        foreach(var grant in grants)
        {
            CapabilityGrant saved=grant is HostCapabilityGrant?after.HostGrants.Single(g=>g.GrantId==grant.GrantId):after.ServerGrants.Single(g=>g.GrantId==grant.GrantId);
            if(saved!=grant)throw new UnauthorizedAccessException("Preset grant changed before commit.");
        }
        foreach(var audit in audits)audit();
        ct.ThrowIfCancellationRequested();tx.Commit();
        return new(batch,after.Revision,Array.AsReadOnly(expansion.Hosts.Select(g=>g.GrantId).ToArray()),Array.AsReadOnly(expansion.Servers.Select(g=>g.GrantId).ToArray()));
    }
}
