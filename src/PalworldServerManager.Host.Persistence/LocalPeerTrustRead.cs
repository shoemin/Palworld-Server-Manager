using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed record LocalPeerTrustItem(Guid PeerHostId,string State,long Incarnation,
    string? CurrentFingerprint,string? PendingFingerprint,bool RecoveryRequired);
public sealed record LocalPeerTrustPage(Guid HostId,long Revision,IReadOnlyList<LocalPeerTrustItem> Peers,Guid? NextAfterPeer);

public sealed partial class GrantPolicyRepository
{
    // Bounded observation, never an execution permit. Later writers recheck all authority.
    public LocalPeerTrustPage ReadLocalPeerTrust(LocalPrincipalMutationActor actor,Guid? afterPeer,int limit,CancellationToken ct=default)
        =>ReadPeerTrust(LocalWriter(actor),afterPeer,limit,ct);
    public LocalPeerTrustPage ReadRemotePeerTrust(PeerGrantMutationActor actor,Guid? afterPeer,int limit,CancellationToken ct=default)
        =>ReadPeerTrust(PeerWriter(actor),afterPeer,limit,ct);
    private LocalPeerTrustPage ReadPeerTrust(GrantWriter writer,Guid? afterPeer,int limit,CancellationToken ct)
    {
        if(limit is <1 or >16||afterPeer==Guid.Empty)throw new ArgumentException("Invalid peer page.");
        ct.ThrowIfCancellationRequested();
        using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        var before=Read(c,tx);writer.Require(c,tx,before);
        AuthorizeOrAudit(c,tx,writer,before.Revision,new("ReadPeerTrust",hostId,null,writer.Actual.Kind==ActorKind.LocalPrincipal?"LocalPeerPage":"RemotePeerPage"),()=>
        {
            if(!before.Policy.CanUseHost(writer.Actual,HostCapability.ManageTrustedManagers,hostId))
                throw new UnauthorizedAccessException("Trust inspection refused.");
            return true;
        },ct);
        var peers=new List<LocalPeerTrustItem>();
        using(var q=Command(c,tx,"""
            SELECT t.PeerHostId,t.State,i.Incarnation,t.CurrentTrustedPublicKeyFingerprint,
                t.PendingTrustedPublicKeyFingerprint,t.PeerRecoveryRequired
            FROM TrustedManagers t LEFT JOIN PeerRelationshipIncarnations i ON i.PeerHostId=t.PeerHostId
            WHERE t.PeerHostId>$after COLLATE BINARY ORDER BY t.PeerHostId COLLATE BINARY LIMIT $limit;
            """,("$after",afterPeer?.ToString("D")??""),("$limit",limit+1)))
        {
            using var r=q.ExecuteReader();
            while(r.Read())
            {
                ct.ThrowIfCancellationRequested();var id=ParseId(r.GetString(0));var state=r.GetString(1);
                if(id==hostId||state is not ("PeerBound" or "Active" or "Revoked")||r.IsDBNull(2)||r.GetInt64(2)<=0)throw Corrupt();
                string? Pin(int n)
                {
                    if(r.IsDBNull(n))return null;var pin=r.GetString(n);
                    return pin.Length==64&&pin.All(ch=>ch is >= '0' and <= '9' or >= 'A' and <= 'F')?pin:throw Corrupt();
                }
                peers.Add(new(id,state,r.GetInt64(2),Pin(3),Pin(4),r.GetBoolean(5)));
            }
        }
        Guid? next=peers.Count>limit?peers[limit-1].PeerHostId:null;
        if(peers.Count>limit)peers.RemoveAt(limit);
        var after=Read(c,tx);writer.Require(c,tx,after);RequireRevision(before.Revision,after.Revision);
        ct.ThrowIfCancellationRequested();tx.Commit();return new(hostId,after.Revision,peers.ToArray(),next);
    }
}
