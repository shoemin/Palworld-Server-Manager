using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed record ReissuedGrant(Guid HistoricalGrantId,Guid NewGrantId);
public sealed record HistoricalGrantReissueResult(Guid? AuditBatchId,long Revision,
    IReadOnlyList<ReissuedGrant> Hosts,IReadOnlyList<ReissuedGrant> Servers);

public sealed partial class GrantPolicyRepository
{
    // Trusted authenticated local channel supplies owner. This does not replace credentials
    // or certify their replacement; it is the ordinary grant action used afterward.
    public HistoricalGrantReissueResult ReissueHistoricalPeerRoots(LocalPrincipalMutationActor owner,long expectedRevision,
        Guid peerId,IEnumerable<Guid> hostRoots,IEnumerable<Guid> serverRoots,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(owner);ArgumentNullException.ThrowIfNull(hostRoots);ArgumentNullException.ThrowIfNull(serverRoots);
        ct.ThrowIfCancellationRequested();var peer=ActorRef.RemoteManager(peerId);
        var hs=hostRoots.ToArray();var ss=serverRoots.ToArray();
        if(hs.Any(id=>id==Guid.Empty)||ss.Any(id=>id==Guid.Empty)||hs.Distinct().Count()!=hs.Length||ss.Distinct().Count()!=ss.Length)
            throw new ArgumentException("Invalid historical grant selection.");
        using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        var before=Read(c,tx);RequireLocal(c,tx,owner,before);RequireRevision(expectedRevision,before.Revision);
        var actual=ActorRef.LocalPrincipal(owner.LocalPrincipalId);
        if(!before.Policy.IsOwner(actual)||!before.Policy.IsActive(peer))throw new UnauthorizedAccessException("Owner and active peer required.");
        var now=time.GetUtcNow();
        var hosts=hs.Select(id=>(Old:before.HostGrants.SingleOrDefault(g=>g.GrantId==id),New:before.Policy.ReissueHostRoot(actual,Guid.NewGuid(),peerId,id,now))).ToArray();
        var servers=ss.Select(id=>(Old:before.ServerGrants.SingleOrDefault(g=>g.GrantId==id),New:before.Policy.ReissueServerRoot(actual,Guid.NewGuid(),peerId,id,now))).ToArray();
        var pairs=hosts.Select(p=>(Old:(CapabilityGrant)p.Old!,New:(CapabilityGrant)p.New))
            .Concat(servers.Select(p=>(Old:(CapabilityGrant)p.Old!,New:(CapabilityGrant)p.New))).ToArray();
        ct.ThrowIfCancellationRequested();
        if(pairs.Length==0)return new(null,before.Revision,Array.Empty<ReissuedGrant>(),Array.Empty<ReissuedGrant>());
        var batch=Guid.NewGuid();
        foreach(var pair in pairs)
        {
            Insert(c,tx,pair.New);
            Audit(c,tx,actual,pair.New,"CapabilityGrantReissued",now,1,"OwnerHistoricalReissue:"+Id(batch)+"; HistoricalGrant="+Id(pair.Old.GrantId));
        }
        var after=Read(c,tx);RequireLocal(c,tx,owner,after);RequireRevision(checked(before.Revision+pairs.Length),after.Revision);
        if(!after.Policy.IsOwner(actual)||!after.Policy.IsActive(peer))throw new UnauthorizedAccessException("Reissue authority changed.");
        foreach(var pair in pairs)
        {
            var rows=pair.New is HostCapabilityGrant?after.HostGrants.Cast<CapabilityGrant>():after.ServerGrants;
            if(rows.Single(g=>g.GrantId==pair.New.GrantId)!=pair.New||rows.Single(g=>g.GrantId==pair.Old.GrantId)!=pair.Old)
                throw new UnauthorizedAccessException("Reissue effect or history changed.");
        }
        ct.ThrowIfCancellationRequested();tx.Commit();
        return new(batch,after.Revision,Array.AsReadOnly(hosts.Select(p=>new ReissuedGrant(p.Old!.GrantId,p.New.GrantId)).ToArray()),
            Array.AsReadOnly(servers.Select(p=>new ReissuedGrant(p.Old!.GrantId,p.New.GrantId)).ToArray()));
    }
}
