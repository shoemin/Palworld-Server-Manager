using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Host;

namespace PalworldServerManager.SelfTest;

internal static partial class AuthenticatedPermissionDispatchTests
{
    public static async Task BothDispatchersRetireNotReadyNotifications()
    {
        foreach(var remote in new[]{false,true})
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var r=new Rig(preparation:async(peer,address,work,ct)=>
            {entered.TrySetResult();await release.Task.WaitAsync(ct);return await work(null!,ct);});
            if(remote)r.Repo.IssueHost(r.F.Actor,r.Revision,Guid.NewGuid(),ActorRef.RemoteManager(r.PeerId),
                HostCapability.ManageTrustedManagers,r.F.HostId,Use,null);
            var preparation=r.Notifications!.Prepare(r.PeerId,new("https://unused.invalid/"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var incarnation=r.F.Count($"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{r.PeerId:D}';");
            var result=remote?
                await r.Peer.Permissions.Invoke(r.PeerContext(),c=>c.RevokePeerWithNotification(r.Revision,r.PeerId,incarnation),default):
                await r.Local.Permissions.Invoke(await r.LocalContext(true),c=>c.RevokePeerWithNotification(r.Revision,r.PeerId,incarnation),default);
            Check(result.Revocation.Changed&&await result.Notification==PeerUnpairNotification.NotAttempted);
            release.TrySetResult();Check(!await preparation.WaitAsync(TimeSpan.FromSeconds(5)));
            Check(r.Repo.Read().Policy.IsOwner(ActorRef.LocalPrincipal(r.F.Owner)));
        }
    }
}
