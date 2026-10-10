using System.Diagnostics;
using System.Security.Authentication;
using System.Security.Principal;
using System.Text.Json;
using Grpc.Core;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using static PalworldServerManager.SelfTest.WindowsPeerProcessFixture;

namespace PalworldServerManager.SelfTest;

internal static class WindowsRecoveryProcessQualification
{
    internal static async Task Run(string serviceRoot,SecurityIdentifier sid,CancellationToken stop)
    {
        for(var mode=1;mode<=5;mode++)await RunCase(serviceRoot,sid,mode,stop);
    }
    private static Guid Approve(FixtureHost local,FixtureHost peer,string localPin,string peerPin,char historical)
    {
        local.Peers.RecordVerifiedBinding(peer.Config.Host,peerPin,localPin);
        using(var c=local.Database.OpenConnection())
        {
            using var q=c.CreateCommand();q.CommandText="UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint=$old,PeerRecoveryRequired=1 WHERE PeerHostId=$peer;";
            q.Parameters.AddWithValue("$old",new string(historical,64));q.Parameters.AddWithValue("$peer",peer.Config.Host.ToString("D"));q.ExecuteNonQuery();
        }
        // Explicit historical binding/Owner proof fixture. Actual PAKE is qualified separately.
        var id=local.Peers.RecordOwnerVerifiedBinding(local.Actor,peer.Config.Host,peerPin,localPin).ReplacementId!.Value;
        var grants=new GrantPolicyRepository(local.Database,local.Config.Host);grants.ApprovePeerReplacement(local.Actor,grants.Read().Revision,id);return id;
    }
    private static async Task Refused(Task task)
    {
        try{await task;}
        catch(Exception ex)when(ex is RpcException or AuthenticationException or OperationCanceledException or IOException){return;}
        throw new Exception("Recovery process qualification: dropped or hung RPC claimed success.");
    }
    private static async Task RunCase(string serviceRoot,SecurityIdentifier sid,int mode,CancellationToken stop)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop);timeout.CancelAfter(TimeSpan.FromSeconds(45));var ct=timeout.Token;
        var parent=Path.GetFullPath(serviceRoot).TrimEnd(Path.DirectorySeparatorChar);var nonce=Guid.NewGuid();
        var root=Path.GetFullPath(Path.Combine(parent,"peer-process-"+nonce.ToString("N")));
        Check(root.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&!Directory.Exists(root),"Unsafe recovery fixture root.");
        var aId=Guid.NewGuid();var bId=Guid.NewGuid();
        var a=new FixtureHost(new(nonce,aId,bId,Guid.NewGuid(),Path.Combine(root,"host-"+aId.ToString("N")),sid.Value));
        var b=new FixtureHost(new(nonce,bId,aId,Guid.NewGuid(),Path.Combine(root,"host-"+bId.ToString("N")),sid.Value,RecoveryFault:mode));
        RequirePath(a.Config);RequirePath(b.Config);using var aLease=Lease(a.Config);
        HostGenerationTransitions? sender=null;Child? receiver=null;var aInitialized=false;var bInitialized=false;Task? active=null;
        try
        {
            var aPin=await a.Initialize(ct);aInitialized=true;string bPin;Guid aApproval,bApproval;
            using(var lease=Lease(b.Config))
            {
                bPin=await b.Initialize(ct);bInitialized=true;
                aApproval=Approve(a,b,aPin,bPin,'E');bApproval=Approve(b,a,bPin,aPin,'F');
                File.WriteAllText(Path.Combine(b.Config.Root,ConfigName),JsonSerializer.Serialize(b.Config));
            }
            sender=a.Create();await sender.StartAsync(ct);receiver=new Child(b.Config);var first=await receiver.Read(ct);
            Check(first.Kind=="ready"&&first.Pin==bPin&&first.CurrentPeerPin==aPin&&first.Recovery is {Required:true,Confirmed:false,Receipts:0},"Initial recovery child state differs.");
            using(var denied=HostExclusivityLock.TryAcquire(TimeSpan.Zero,b.Config.Mutex))Check(denied is null,"Child lease is not held.");
            var reverse=mode is 3 or 4;var watch=Stopwatch.StartNew();
            active=reverse?sender.PullRecoveryAsync(bId,first.Address,ct):sender.ConfirmRecoveryAsync(bId,first.Address,ct);
            var barrier=await receiver.Read(ct);
            Check(barrier.Kind=="recovery-barrier"&&barrier.Instance==first.Instance&&!active.IsCompleted,"Actual receiving barrier did not withhold completion.");
            Check(barrier.Recovery!.Approval==bApproval&&barrier.Recovery.Receipts==(mode==2?1:0)&&barrier.Recovery.Confirmed==(mode==4),"Barrier canonical state differs.");
            if(mode==5)
            {
                await Refused(active);Check(watch.Elapsed>=TimeSpan.FromSeconds(13)&&watch.Elapsed<TimeSpan.FromSeconds(23),"Actual hung peer did not respect fixed client deadline.");
                var alive=await receiver.Command("recovery-state",first.Address,ct);
                Check(alive.Instance==first.Instance&&alive.Recovery is {Required:true,Confirmed:false,Receipts:0},"Hung child exited or fabricated a receipt.");
            }
            await receiver.Kill(requireRunning:true);await Refused(active);active=null;receiver.Dispose();receiver=null;
            using(var lease=Lease(b.Config))
            {
                var state=RecoveryState(b);Check(state.Approval==bApproval&&state.Receipts==(mode==2?1:0)&&state.Required==(mode!=2)&&state.Confirmed==(mode==4),"Remote crash lost or fabricated durable state.");
                var own=RecoveryState(a);Check(own.Approval==aApproval&&!own.Confirmed&&own.Receipts==(reverse?1:0)&&own.Required==!reverse,"Local independent crash state differs.");
                File.WriteAllText(Path.Combine(b.Config.Root,ConfigName),JsonSerializer.Serialize(b.Config with{RecoveryFault=6}));
            }
            receiver=new Child(b.Config);var restart=await receiver.Read(ct);
            Check(restart.Instance!=first.Instance&&restart.Key==first.Key&&restart.Pin==first.Pin,"Restart did not retain actual protected/native identity.");
            if(reverse)Check(await sender.PullRecoveryAsync(bId,restart.Address,ct)==(mode==4?PeerRecoveryPullExchange.NoPending:PeerRecoveryPullExchange.Confirmed),"Reverse retry claimed wrong outcome.");
            else Check(await sender.ConfirmRecoveryAsync(bId,restart.Address,ct)==PeerRecoveryCompletionExchange.Confirmed,"Original acknowledgment retry did not confirm.");
            var final=await receiver.Command("recovery-state",restart.Address,ct);var local=RecoveryState(a);var remote=final.Recovery!;
            Check(remote.Approval==bApproval&&local.Approval==aApproval,"Retry replaced the original approval.");
            if(reverse)
                Check(local.Receipts==1&&local.ReceiptApproval==bApproval&&local.ReceivedAudits==1&&!local.Required&&!local.Confirmed&&remote.Confirmed&&remote.ConfirmationAudits==1&&remote.Required&&remote.Receipts==0,"Reverse independent state/audit changed.");
            else
                Check(remote.Receipts==1&&remote.ReceiptApproval==aApproval&&remote.ReceivedAudits==1&&!remote.Required&&!remote.Confirmed&&local.Confirmed&&local.ConfirmationAudits==1&&local.Required&&local.Receipts==0,"Forward independent state/audit changed.");
            await receiver.Stop(ct);receiver.Dispose();receiver=null;
            using(var lease=Lease(b.Config))Check(RecoveryState(b)==remote,"Orderly exit lost recovery state.");
        }
        finally
        {
            try{if(receiver is not null){await receiver.Kill();receiver.Dispose();}}
            finally
            {
                if(active is not null){try{await active;}catch{}}
                if(sender is not null)await sender.StopAsync();
            }
            // Never delete possibly borrowed native material after a failure to drain.
            using var bLease=Lease(b.Config);
            if(bInitialized)await b.Cleanup();if(aInitialized)await a.Cleanup();
            if(Directory.Exists(root))
            {
                Check(Path.GetFullPath(root).StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&(File.GetAttributes(root)&FileAttributes.ReparsePoint)==0,"Unsafe recovery cleanup root.");
                Directory.Delete(root,false);
            }
        }
        Check(!Directory.Exists(root),"Recovery fixture cleanup incomplete.");
        Console.WriteLine($"PASS actual recovery process case{mode}: durable original approval retry, native identity continuity and complete cleanup.");
    }
}
