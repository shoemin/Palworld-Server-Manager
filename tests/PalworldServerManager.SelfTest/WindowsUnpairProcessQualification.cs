using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using static PalworldServerManager.SelfTest.WindowsPeerProcessFixture;

namespace PalworldServerManager.SelfTest;

internal static class WindowsUnpairProcessQualification
{
    internal static async Task Run(string serviceRoot,SecurityIdentifier sid,CancellationToken stop)
    {
        for(var mode=1;mode<=4;mode++)await RunCase(serviceRoot,sid,mode,stop);
    }
    private static async Task RunCase(string serviceRoot,SecurityIdentifier sid,int mode,CancellationToken stop)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop);timeout.CancelAfter(TimeSpan.FromSeconds(35));var ct=timeout.Token;
        var parent=Path.GetFullPath(serviceRoot).TrimEnd(Path.DirectorySeparatorChar);var nonce=Guid.NewGuid();
        var root=Path.GetFullPath(Path.Combine(parent,"peer-process-"+nonce.ToString("N")));
        Check(root.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&!Directory.Exists(root),"Unsafe unpair fixture root.");
        var aId=Guid.NewGuid();var bId=Guid.NewGuid();
        var a=new FixtureHost(new(nonce,aId,bId,Guid.NewGuid(),Path.Combine(root,"host-"+aId.ToString("N")),sid.Value));
        var b=new FixtureHost(new(nonce,bId,aId,Guid.NewGuid(),Path.Combine(root,"host-"+bId.ToString("N")),sid.Value,UnpairFault:mode));
        RequirePath(a.Config);RequirePath(b.Config);using var aLease=Lease(a.Config);
        HostNetworkGeneration? sender=null;Child? receiver=null;var aInitialized=false;var bInitialized=false;Task<PeerUnpairNotification>? active=null;
        try
        {
            var aPin=await a.Initialize(ct);aInitialized=true;string bPin;
            using(var lease=Lease(b.Config))
            {
                bPin=await b.Initialize(ct);bInitialized=true;
                a.Peers.RecordVerifiedBinding(bId,bPin,aPin);b.Peers.RecordVerifiedBinding(aId,aPin,bPin);
                File.WriteAllText(Path.Combine(b.Config.Root,ConfigName),JsonSerializer.Serialize(b.Config));
            }
            sender=await UnpairGeneration(a,null,ct);receiver=new Child(b.Config);var first=await receiver.Read(ct);
            Check(first.Kind=="ready"&&first.Pin==bPin&&first.CurrentPeerPin==aPin&&first.Unpair is {State:"PeerBound",Receipts:0},"Initial unpair child state differs.");
            using(var denied=HostExclusivityLock.TryAcquire(TimeSpan.Zero,b.Config.Mutex))Check(denied is null,"Unpair child lease is not held.");
            await sender.ActivateAsync(bId,first.Address,ct);
            var original=(await receiver.Command("unpair-state",first.Address,ct)).Unpair!;var before=UnpairState(a);
            Check(original is {State:"Active",Activations:1}&&before is {State:"Active",Activations:1},"Real activation did not complete.");
            var coordinator=sender.UnpairNotifications!;Check(await coordinator.Prepare(bId,first.Address,ct),"Unpair connection not prepared.");
            var grants=new GrantPolicyRepository(a.Database,aId);using var call=new LocalPermissionCall(grants,a.Actor,ct,coordinator);
            var watch=Stopwatch.StartNew();var commit=call.RevokePeerWithNotification(grants.Read().Revision,bId,before.Incarnation);active=commit.Notification;
            Check(commit.Revocation.Changed&&UnpairState(a) is {State:"Revoked",Receipts:0,LocalAudits:1,PeerAudits:0,Activations:1},"Local revoke did not commit independently.");
            if(mode!=4)
            {
                // The synchronous local call must return while the actual remote handler is held.
                var barrier=await receiver.Read(ct);
                Check(barrier.Kind=="unpair-barrier"&&barrier.Instance==first.Instance&&!active.IsCompleted,"Actual unpair response was not withheld.");
                Check(barrier.Unpair!.State==(mode==2?"Revoked":"Active")&&barrier.Unpair.Receipts==(mode==2?1:0),"Unpair barrier state differs.");
                if(mode==3)
                {
                    Check(await active==PeerUnpairNotification.Unconfirmed&&watch.Elapsed>=TimeSpan.FromSeconds(2)&&watch.Elapsed<TimeSpan.FromSeconds(8),"Hung receiver bypassed fixed three-second deadline.");
                    var alive=await receiver.Command("unpair-state",first.Address,ct);
                    Check(alive.Instance==first.Instance&&alive.Unpair==original,"Hung child exited or fabricated a receipt.");
                }
            }
            else Check(await active==PeerUnpairNotification.Confirmed,"Delivered unpair was not confirmed.");
            await receiver.Kill(requireRunning:true);
            Check(await active==(mode==4?PeerUnpairNotification.Confirmed:PeerUnpairNotification.Unconfirmed),"Dropped unpair claimed confirmation.");
            active=null;receiver.Dispose();receiver=null;
            var local=UnpairState(a);UnpairProcessState remote;
            using(var lease=Lease(b.Config))
            {
                remote=UnpairState(b);var received=mode is 2 or 4;
                Check(remote.State==(received?"Revoked":"Active")&&remote.Incarnation==original.Incarnation+(received?1:0)&&remote.Receipts==(received?1:0)&&
                    remote.LocalAudits==0&&remote.PeerAudits==(received?1:0)&&remote.Activations==1,"Receiver durable state/audit differs.");
                Check(local.Incarnation==before.Incarnation+1,"Local revocation incarnation differs.");
                File.WriteAllText(Path.Combine(b.Config.Root,ConfigName),JsonSerializer.Serialize(b.Config with{UnpairFault=4}));
            }
            receiver=new Child(b.Config);var restart=await receiver.Read(ct);
            Check(restart.Instance!=first.Instance&&restart.Key==first.Key&&restart.Pin==first.Pin&&restart.Unpair==remote,"Restart lost native identity or durable unpair state.");
            Check(!await coordinator.Prepare(bId,restart.Address,ct),"Revoked sender created a new notification connection.");
            Check(await coordinator.Notify(commit.Revocation)==PeerUnpairNotification.NotAttempted,"Consumed notification was retried.");
            var refused=await receiver.Command("unpair-refuse-activation",sender.Endpoints!.Value.Peer,ct);
            Check(refused.Kind=="unpair-refused"&&refused.Unpair==remote&&UnpairState(a)==local,"Stale reconnect resurrected state or duplicated revocation.");
            await receiver.Stop(ct);receiver.Dispose();receiver=null;
            using(var lease=Lease(b.Config))Check(UnpairState(b)==remote,"Orderly exit lost durable unpair state.");
        }
        finally
        {
            try{if(receiver is not null){await receiver.Kill();receiver.Dispose();}}
            finally
            {
                if(active is not null){try{await active;}catch{}}
                if(sender is not null)await sender.StopAsync();
            }
            // Release native credentials only after both process exit and generation drain.
            using var bLease=Lease(b.Config);
            if(bInitialized)await b.Cleanup();if(aInitialized)await a.Cleanup();
            if(Directory.Exists(root))
            {
                Check(Path.GetFullPath(root).StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&(File.GetAttributes(root)&FileAttributes.ReparsePoint)==0,"Unsafe unpair cleanup root.");
                Directory.Delete(root,false);
            }
        }
        Check(!Directory.Exists(root),"Unpair fixture cleanup incomplete.");
        Console.WriteLine($"PASS actual unpair process case{mode}: independent durable revocation, honest delivery, stale reconnect refusal and complete cleanup.");
    }
}
