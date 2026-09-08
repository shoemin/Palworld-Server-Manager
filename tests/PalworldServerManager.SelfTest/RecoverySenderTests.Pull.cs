using System.Diagnostics;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using static PalworldServerManager.SelfTest.PeerSecurityRpcTests;

namespace PalworldServerManager.SelfTest;

internal static partial class RecoverySenderTests
{
    private static bool PullMethod(HttpRequestMessage request,string method)=>request.RequestUri!.AbsolutePath.EndsWith("/"+method,StringComparison.Ordinal);
    private static PeerGrantMutationActor CurrentPullProof(Fixture local,Fixture peer)=>new(local.State.HostId,peer.State.HostId,peer.Pin,local.Pin,Incarnation(local,peer));
    public static async Task PullActualIndependentRecoveryAndFreshConnections()
    {
        await using var f=new Pair();await f.Start();var tracked=new TrackingFactory(f.A.Certificate.Value);
        Check(await f.Sender(f.A,tracked).PullAsync(f.B.State.HostId,f.B.Address)==PeerRecoveryPullExchange.Confirmed);
        Check(tracked.Observed==2&&tracked.Disposed==2&&tracked.ReceiptAttempts==0);
        Check(!f.A.State.Repository.Read(f.B.State.HostId)!.RecoveryRequired&&f.B.State.Repository.Read(f.A.State.HostId)!.RecoveryRequired);
        Check(Confirmed(f.B)&&!Confirmed(f.A)&&CountEvent(f.A,"PeerRecoveryCompletionReceived")==1&&CountEvent(f.B,"PeerRecoveryCompletionConfirmed")==1);
        var before=OfferSnapshot(f.A)+OfferSnapshot(f.B);
        Check(await f.Sender(f.A,tracked).PullAsync(f.B.State.HostId,f.B.Address)==PeerRecoveryPullExchange.NoPending&&before==OfferSnapshot(f.A)+OfferSnapshot(f.B));
        Check(tracked.Observed==3&&tracked.Disposed==3);
        Check(await f.Sender(f.B).PullAsync(f.A.State.HostId,f.A.Address)==PeerRecoveryPullExchange.Confirmed);
        Check(!f.B.State.Repository.Read(f.A.State.HostId)!.RecoveryRequired&&Confirmed(f.A)&&Confirmed(f.B));
    }
    public static async Task PullReceiptValidationIsReadOnlyAndExact()
    {
        await using var f=new Pair();var repo=Grants(f.A);var source=CurrentPullProof(f.A,f.B);var approval=f.BApproval;
        await Failed(Task.Run(()=>repo.RequireRecordedRecoveryCompletion(source,approval)));
        var recorded=repo.ReceiveAuthenticatedRecoveryCompletion(source,approval,f.A.Pin);var current=source with{Incarnation=recorded.Incarnation};
        var before=OfferSnapshot(f.A);repo.RequireRecordedRecoveryCompletion(current,approval);Check(before==OfferSnapshot(f.A));
        await Failed(Task.Run(()=>repo.RequireRecordedRecoveryCompletion(source,approval)));
        await Failed(Task.Run(()=>repo.RequireRecordedRecoveryCompletion(current,Guid.NewGuid())));
        using var canceled=new CancellationTokenSource();canceled.Cancel();await Failed(Task.Run(()=>repo.RequireRecordedRecoveryCompletion(current,approval,canceled.Token)));
        foreach(var proof in new[]{current with{HostId=f.B.State.HostId},current with{PeerHostId=f.A.State.HostId},current with{LocalFingerprint=new string('F',64)},current with{PeerFingerprint=new string('E',64)}})
            await Failed(Task.Run(()=>repo.RequireRecordedRecoveryCompletion(proof,approval)));
        Check(before==OfferSnapshot(f.A));
        var later=Guid.NewGuid();repo.ReceiveAuthenticatedRecoveryCompletion(current,later,f.A.Pin);before=OfferSnapshot(f.A);
        await Failed(Task.Run(()=>repo.RequireRecordedRecoveryCompletion(current,approval)));repo.RequireRecordedRecoveryCompletion(current,later);Check(before==OfferSnapshot(f.A));
    }
    public static async Task PullLostStagesRetryOriginalReceiptAfterListenerRestart()
    {
        for(var mode=0;mode<3;mode++)
        {
            await using var f=new Pair();await f.Start();var tracked=new TrackingFactory(f.A.Certificate.Value);var selected=mode;
            tracked.After=(request,response,ct)=>
            {
                if((selected==0&&PullMethod(request,"ReadRecoveryCompletionOffer"))||(selected==1&&tracked.Observed==2&&PullMethod(request,"NegotiateRecovery"))||
                    (selected==2&&PullMethod(request,"ConfirmRecoveryCompletionOffer")))throw new IOException("Controlled test response loss.");
                return Task.FromResult(response);
            };
            await Failed(f.Sender(f.A,tracked).PullAsync(f.B.State.HostId,f.B.Address));
            Check(CountEvent(f.A,"PeerRecoveryCompletionReceived")==(mode==0?0:1)&&Confirmed(f.B)==(mode==2));
            Check(tracked.Disposed==(mode==0?1:2));
            await f.B.Stop();await f.B.Start();var restarted=new PeerSecurityRpcRuntime(f.A.State.Database,f.A.State.HostId,f.A.Runtime.Hook,f.A.State.Time);
            var retry=new PeerRecoveryCompletionRpcClient(restarted,new PalworldServerManager.Platform.Windows.WindowsPeerHttpTransportFactory(f.A.Certificate.Value));
            Check(await retry.PullAsync(f.B.State.HostId,f.B.Address)==(mode==2?PeerRecoveryPullExchange.NoPending:PeerRecoveryPullExchange.Confirmed));
            Check(CountEvent(f.A,"PeerRecoveryCompletionReceived")==1&&CountEvent(f.B,"PeerRecoveryCompletionConfirmed")==1);
            Check(HostDatabase.QueryScalarLong(f.A.State.Writer,$"SELECT COUNT(*) FROM PeerRecoveryCompletionReceipts WHERE ApprovalId='{f.BApproval:D}';")==1);
        }
    }
    public static async Task PullChangedAuthorityAndSupersededReceiptNeverAttest()
    {
        for(var mode=0;mode<6;mode++)
        {
            await using var f=new Pair();await f.Start();var tracked=new TrackingFactory(f.A.Certificate.Value);var selected=mode;Guid newer=Guid.NewGuid();
            tracked.Observe=_=>
            {
                if(tracked.Observed!=2)return;
                if(selected==0)f.A.State.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{f.B.State.HostId:D}'; UPDATE TrustedManagers SET PeerRecoveryRequired=0 WHERE PeerHostId='{f.B.State.HostId:D}';");
                if(selected==1)f.A.State.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{new string('E',64)}' WHERE CredentialRef='current';");
                if(selected==2)f.A.State.Execute($"UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='{new string('F',64)}' WHERE PeerHostId='{f.B.State.HostId:D}';");
                if(selected==3)f.A.State.Execute("UPDATE LocalPrincipals SET IsOwner=0 WHERE IsOwner=1;");
                if(selected==4)Grants(f.A).ReceiveAuthenticatedRecoveryCompletion(CurrentPullProof(f.A,f.B),newer,f.A.Pin);
                if(selected==5)
                {
                    f.B.State.Execute($"UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='{new string('E',64)}' WHERE PeerHostId='{f.A.State.HostId:D}';");
                    Approve(f.B,f.A);
                }
            };
            await Failed(f.Sender(f.A,tracked).PullAsync(f.B.State.HostId,f.B.Address));Check(!Confirmed(f.B)&&tracked.Disposed==2&&CountEvent(f.B,"PeerRecoveryCompletionConfirmed")==0);
            if(mode==4)Check(HostDatabase.QueryScalarLong(f.A.State.Writer,$"SELECT COUNT(*) FROM PeerRecoveryCompletionReceipts WHERE ApprovalId='{newer:D}';")==1);
        }
    }
    public static async Task PullMalformedOffersAndMismatchAudit()
    {
        for(var mode=0;mode<5;mode++)
        {
            await using var f=new Pair();await f.Start();var tracked=new TrackingFactory(f.A.Certificate.Value);var selected=mode;
            tracked.After=async(request,response,ct)=>
            {
                if(!PullMethod(request,"ReadRecoveryCompletionOffer"))return response;var offer=await Message(response,PeerRecoveryOfferReply.Parser,ct);
                if(selected==0)offer.OfferingHostId=f.A.State.HostId.ToString("D");if(selected==1)offer.Acknowledgment.ReceivingHostId=f.B.State.HostId.ToString("D");
                if(selected==2)offer.Acknowledgment.ApprovalId="bad";if(selected==3)offer.Acknowledgment.AcknowledgedFingerprint="bad";
                if(selected==4)offer.Acknowledgment.AcknowledgedFingerprint=new string('E',64);return Rewrite(response,offer);
            };
            if(mode==4)Check(await f.Sender(f.A,tracked).PullAsync(f.B.State.HostId,f.B.Address)==PeerRecoveryPullExchange.KeyMismatch);
            else await Failed(f.Sender(f.A,tracked).PullAsync(f.B.State.HostId,f.B.Address));
            Check(f.A.State.Repository.Read(f.B.State.HostId)!.RecoveryRequired&&!Confirmed(f.B)&&tracked.Disposed==1&&f.A.State.Count("PeerRecoveryCompletionReceipts")==0);
            Check(CountEvent(f.A,"PeerRecoveryCompletionKeyMismatch")== (mode==4?1:0));
        }
    }
    public static async Task PullMalformedConfirmationAndPostCommitChangeRefuse()
    {
        for(var mode=0;mode<5;mode++)
        {
            await using var f=new Pair();await f.Start();var tracked=new TrackingFactory(f.A.Certificate.Value);var selected=mode;
            tracked.After=async(request,response,ct)=>
            {
                if(!PullMethod(request,"ConfirmRecoveryCompletionOffer"))return response;
                var reply=await Message(response,PeerRecoveryOfferConfirmationReply.Parser,ct);
                if(selected==0)reply.Result=(PeerRecoveryOfferConfirmationResult)999;if(selected==1)reply.Receipt.ApprovalId=Guid.NewGuid().ToString("D");
                if(selected==2)reply.Receipt.Result=PalworldServerManager.Contracts.Wire.PeerRecoveryCompletionResult.KeyMismatch;
                if(selected==3)reply.Receipt=null;
                if(selected==4)f.A.State.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{f.B.State.HostId:D}';");
                return Rewrite(response,reply);
            };
            await Failed(f.Sender(f.A,tracked).PullAsync(f.B.State.HostId,f.B.Address));
            Check(Confirmed(f.B)&&tracked.Disposed==2&&CountEvent(f.A,"PeerRecoveryCompletionReceived")==1&&CountEvent(f.B,"PeerRecoveryCompletionConfirmed")==1);
        }
    }
    public static async Task PullNegotiationNoPendingAndInputBounds()
    {
        for(var mode=0;mode<6;mode++)
        {
            await using var f=new Pair();await f.Start();var tracked=new TrackingFactory(f.A.Certificate.Value);var selected=mode;
            tracked.After=async(request,response,ct)=>
            {
                if(!PullMethod(request,"NegotiateRecovery"))return response;var hello=await Message(response,PeerHello.Parser,ct);
                if(selected==0)hello.Handshake.Capabilities.Remove(FeatureCapability.PeerRecoveryCompletionOffer);
                if(selected==1)hello.Handshake.Capabilities.Remove(FeatureCapability.PeerRecoveryCompletion);
                if(selected==2)hello.Host.HostId=f.A.State.HostId.ToString("D");if(selected==3)hello.Handshake.Protocol.Major=2;
                if(selected==4)hello.Handshake.ProductVersion=new string('X',257);if(selected==5)hello.Handshake.Protocol.Minor=1;
                return Rewrite(response,hello);
            };
            if(mode==0)Check(await f.Sender(f.A,tracked).PullAsync(f.B.State.HostId,f.B.Address)==PeerRecoveryPullExchange.Unsupported);
            else if(mode==5)Check(await f.Sender(f.A,tracked).PullAsync(f.B.State.HostId,f.B.Address)==PeerRecoveryPullExchange.Confirmed);
            else await Failed(f.Sender(f.A,tracked).PullAsync(f.B.State.HostId,f.B.Address));
            Check(tracked.Disposed==(mode==5?2:1)&&Confirmed(f.B)==(mode==5));
            if(mode!=5)Check(f.A.State.Count("PeerRecoveryCompletionReceipts")==0);
        }
        await using var empty=new Pair(false);await empty.Start();var before=OfferSnapshot(empty.A)+OfferSnapshot(empty.B);
        Check(await empty.Sender(empty.A).PullAsync(empty.B.State.HostId,empty.B.Address)==PeerRecoveryPullExchange.NoPending&&before==OfferSnapshot(empty.A)+OfferSnapshot(empty.B));
        foreach(var uri in new[]{new Uri("http://localhost"),new Uri("https://user@localhost/"),new Uri("https://localhost/path"),new Uri("https://localhost/?q=1"),new Uri("https://localhost/#x")})
            Check(await Failed(empty.Sender(empty.A).PullAsync(empty.B.State.HostId,uri)) is ArgumentException);
        using var canceled=new CancellationTokenSource();canceled.Cancel();Check(await Failed(empty.Sender(empty.A).PullAsync(empty.B.State.HostId,empty.B.Address,canceled.Token)) is OperationCanceledException);
    }
    public static async Task PullGenerationStopDrainsSecondNativeCallback()
    {
        await using var f=new Pair();await f.B.Start();var tracked=new TrackingFactory(f.A.Certificate.Value);var entered=Signal();using var release=new ManualResetEventSlim();
        tracked.Observe=_=>{if(tracked.Observed==2){entered.TrySetResult();release.Wait();}};
        await using var generation=RecoveryGeneration(f.A,f.A.Certificate.Value,tracked);await generation.StartAsync(default);
        var call=Task.Run(()=>generation.PullRecoveryAsync(f.B.State.HostId,f.B.Address));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var stop=generation.StopAsync();await Task.Delay(100);
            Check(!stop.IsCompleted&&!call.IsCompleted&&tracked.Disposed==1&&f.A.Certificate.Value.Handle!=IntPtr.Zero);
            release.Set();await Failed(call);await stop.WaitAsync(TimeSpan.FromSeconds(10));
            await Failed(Task.Run(()=>generation.PullRecoveryAsync(f.B.State.HostId,f.B.Address)));
        }
        finally{release.Set();try{await call;}catch{}}
        Check(tracked.Disposed==2&&f.A.Certificate.Value.Handle==IntPtr.Zero&&!Confirmed(f.B)&&CountEvent(f.A,"PeerRecoveryCompletionReceived")==1);
    }
    public static async Task PullSharedDeadlineSpansBothConnections()
    {
        await using var f=new Pair();await f.Start();var tracked=new TrackingFactory(f.A.Certificate.Value);var second=Signal();var watch=Stopwatch.StartNew();
        tracked.After=async(request,response,ct)=>
        {
            if(PullMethod(request,"ReadRecoveryCompletionOffer"))await Task.Delay(TimeSpan.FromSeconds(8),ct);
            if(PullMethod(request,"ConfirmRecoveryCompletionOffer")){second.TrySetResult();await Task.Delay(Timeout.Infinite,ct);}return response;
        };
        var call=f.Sender(f.A,tracked).PullAsync(f.B.State.HostId,f.B.Address);
        try
        {
            await second.Task.WaitAsync(TimeSpan.FromSeconds(13));
            await Failed(call.WaitAsync(TimeSpan.FromSeconds(15)));watch.Stop();
        }
        finally{try{await call;}catch{}} // Even an assertion/wait failure must drain before fixture key disposal.
        Check(watch.Elapsed<TimeSpan.FromSeconds(22)&&tracked.Disposed==2&&Confirmed(f.B)&&CountEvent(f.A,"PeerRecoveryCompletionReceived")==1);
    }
    public static async Task PullConcurrentClientsStayIdempotent()
    {
        await using var f=new Pair();await f.Start();
        async Task Attempt(){try{await f.Sender(f.A).PullAsync(f.B.State.HostId,f.B.Address);}catch(Exception e)when(e is AuthenticationException or Grpc.Core.RpcException){}}
        await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Attempt()));
        var outcome=await f.Sender(f.A).PullAsync(f.B.State.HostId,f.B.Address);
        Check(outcome is PeerRecoveryPullExchange.Confirmed or PeerRecoveryPullExchange.NoPending);
        Check(Confirmed(f.B)&&CountEvent(f.B,"PeerRecoveryCompletionConfirmed")==1&&CountEvent(f.A,"PeerRecoveryCompletionReceived")==1);
    }
}
