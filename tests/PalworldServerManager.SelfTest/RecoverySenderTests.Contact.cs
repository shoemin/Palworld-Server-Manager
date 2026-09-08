using System.Security.Authentication;
using Grpc.Core;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;
using PalworldServerManager.Platform.Contracts;
using Fixture=PalworldServerManager.SelfTest.PeerSecurityRpcTests.Fixture;

namespace PalworldServerManager.SelfTest;

internal static partial class RecoverySenderTests
{
    private static PeerRecoveryContact Contact(Pair f)=>new(f.B.State.HostId,f.B.Address,new(f.A.Pin,f.B.Pin));
    public static async Task ContactGenerationBothDirectionsWithoutRecursion()
    {
        await using var f=new Pair();await f.B.Start();var tracked=new TrackingFactory(f.A.Certificate.Value);
        await using var generation=RecoveryGeneration(f.A,f.A.Certificate.Value,tracked);await generation.StartAsync(default);
        Check(tracked.Observed==0); // Startup does not replay a durable marker by itself.
        // Trusted contact input is a fixture here; native negotiation trigger is tested separately.
        var result=await generation.RecoveryContacts!.Notify(Contact(f)).WaitAsync(TimeSpan.FromSeconds(8));
        Check(result==PeerRecoveryContactOutcome.AttemptFinished&&Confirmed(f.A)&&Confirmed(f.B));
        Check(!f.A.State.Repository.Read(f.B.State.HostId)!.RecoveryRequired&&!f.B.State.Repository.Read(f.A.State.HostId)!.RecoveryRequired);
        Check(tracked.Observed==3&&tracked.Disposed==3&&tracked.ReceiptAttempts==1);
        Check(CountEvent(f.A,"PeerRecoveryCompletionReceived")==1&&CountEvent(f.B,"PeerRecoveryCompletionReceived")==1);
        await Task.Delay(50);Check(tracked.Observed==3);await generation.StopAsync();Check(f.A.Certificate.Value.Handle==IntPtr.Zero);
    }
    public static async Task ContactFailedPushStillPullsAndNewContactRetries()
    {
        await using var f=new Pair();await f.B.Start();var tracked=new TrackingFactory(f.A.Certificate.Value);var lost=false;
        tracked.After=(request,response,ct)=>
        {
            if(!lost&&PullMethod(request,"NegotiateRecovery")){lost=true;throw new RpcException(new(StatusCode.Unavailable,"fixture first direction loss"));}
            return Task.FromResult(response);
        };
        await using var generation=RecoveryGeneration(f.A,f.A.Certificate.Value,tracked);await generation.StartAsync(default);
        await generation.RecoveryContacts!.Notify(Contact(f)).WaitAsync(TimeSpan.FromSeconds(8));
        Check(lost&&!Confirmed(f.A)&&Confirmed(f.B)&&!f.A.State.Repository.Read(f.B.State.HostId)!.RecoveryRequired);
        Check(f.B.State.Repository.Read(f.A.State.HostId)!.RecoveryRequired&&tracked.Observed==3&&tracked.Disposed==3);
        await Task.Delay(50);Check(tracked.Observed==3); // No self-generated retries after failure.
        await generation.RecoveryContacts.Notify(Contact(f)).WaitAsync(TimeSpan.FromSeconds(8));
        Check(Confirmed(f.A)&&Confirmed(f.B)&&tracked.Observed==5&&tracked.Disposed==5);
        Check(CountEvent(f.A,"PeerRecoveryCompletionReceived")==1&&CountEvent(f.B,"PeerRecoveryCompletionReceived")==1);
    }
    public static async Task ContactRuntimeRequiresCurrentKeysAndOwner()
    {
        await using var f=new Pair();await f.B.Start();var attempts=0;
        await using var coordinator=new PeerRecoveryContactCoordinator(f.A.State.HostId,(contact,ct)=>{Interlocked.Increment(ref attempts);return Task.CompletedTask;});
        await using(var foreign=new PeerRecoveryContactCoordinator(Guid.NewGuid(),(_,_)=>Task.CompletedTask))
            Check(await Failed(Task.Run(()=>f.A.Runtime.ConfigureRecoveryContacts(foreign))) is ArgumentException);
        f.A.Runtime.ConfigureRecoveryContacts(coordinator);
        Check(await Failed(Task.Run(()=>f.A.Runtime.ConfigureRecoveryContacts(coordinator))) is InvalidOperationException);
        var contact=Contact(f);var before=OfferSnapshot(f.A)+OfferSnapshot(f.B);
        Check(await f.A.Runtime.AuthenticatedRecoveryContact(contact.Peer,contact.Address,contact.Identity,default)==PeerRecoveryContactOutcome.AttemptFinished&&attempts==1);
        foreach(var identity in new[]{new PeerTlsConnectionIdentity(new string('E',64),f.B.Pin),new PeerTlsConnectionIdentity(f.A.Pin,new string('F',64))})
            await Failed(Task.Run(()=>f.A.Runtime.AuthenticatedRecoveryContact(contact.Peer,contact.Address,identity,default)));
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Failed(Task.Run(()=>f.A.Runtime.AuthenticatedRecoveryContact(contact.Peer,contact.Address,contact.Identity,canceled.Token)));
        Check(attempts==1&&before==OfferSnapshot(f.A)+OfferSnapshot(f.B));
        foreach(var state in new[]{"PeerBound","Revoked"})
        {
            f.A.State.Execute($"UPDATE TrustedManagers SET State='{state}';");
            Check(await f.A.Runtime.AuthenticatedRecoveryContact(contact.Peer,contact.Address,contact.Identity,default)==PeerRecoveryContactOutcome.NotScheduled&&attempts==1);
        }
        f.A.State.Execute("UPDATE LocalPrincipals SET IsOwner=0;");
        var refused=false;
        try{await f.A.Runtime.AuthenticatedRecoveryContact(contact.Peer,contact.Address,contact.Identity,default);}
        catch(InvalidDataException){refused=true;}
        Check(refused&&attempts==1);
    }
    public static async Task ContactGenerationStopDrainsHeldNativeCallback()
    {
        await using var f=new Pair();await f.B.Start();var tracked=new TrackingFactory(f.A.Certificate.Value);var entered=Signal();using var release=new ManualResetEventSlim();
        tracked.Observe=_=>{if(tracked.Observed==2){entered.TrySetResult();release.Wait();}};
        await using var generation=RecoveryGeneration(f.A,f.A.Certificate.Value,tracked);await generation.StartAsync(default);
        var contact=Contact(f);var work=generation.RecoveryContacts!.Notify(contact);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var stop=generation.StopAsync();await Task.Delay(80);
            Check(!work.IsCompleted&&!stop.IsCompleted&&tracked.Disposed==1&&f.A.Certificate.Value.Handle!=IntPtr.Zero);
            release.Set();await work;await stop.WaitAsync(TimeSpan.FromSeconds(8));
            Check(tracked.Disposed==2&&f.A.Certificate.Value.Handle==IntPtr.Zero&&Confirmed(f.A)&&!Confirmed(f.B));
            Check(await generation.RecoveryContacts.Notify(contact)==PeerRecoveryContactOutcome.NotScheduled);
        }
        finally{release.Set();await work;}
    }
    public static async Task ContactActualSecurityNegotiationTriggersOnlyAfterProof()
    {
        await using var f=new Pair(false);await f.B.Start();var captured=Signal();PeerRecoveryContact? observed=null;var calls=0;
        await using var coordinator=new PeerRecoveryContactCoordinator(f.A.State.HostId,(contact,ct)=>
        {observed=contact;Interlocked.Increment(ref calls);captured.TrySetResult();return Task.CompletedTask;});
        f.A.Runtime.ConfigureRecoveryContacts(coordinator);var tracked=new TrackingFactory(f.A.Certificate.Value);
        var client=new PeerRotationReceiptRpcClient(f.A.Runtime,tracked);
        Check(await client.ConfirmAsync(f.B.State.HostId,f.B.Address)==PeerRotationReceiptExchange.NoReceiptPending);
        await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(calls==1&&observed==Contact(f)&&tracked.Observed==1&&tracked.Disposed==1);
        for(var mode=0;mode<2;mode++)
        {
            var selected=mode;
            tracked.After=async(request,response,ct)=>
            {
                if(!PullMethod(request,"Negotiate"))return response;var hello=await Message(response,PeerHello.Parser,ct);
                if(selected==0)hello.Host.HostId=f.A.State.HostId.ToString("D");else hello.Handshake.Capabilities.Remove(FeatureCapability.PeerRotationReceipt);
                return Rewrite(response,hello);
            };
            await Failed(client.ConfirmAsync(f.B.State.HostId,f.B.Address));Check(calls==1);
        }
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Failed(client.ConfirmAsync(f.B.State.HostId,f.B.Address,canceled.Token));Check(tracked.Observed==3&&tracked.Disposed==3&&calls==1);
        Check(f.A.State.Count("PeerRecoveryCompletionReceipts")==0&&f.B.State.Count("PeerRecoveryCompletionReceipts")==0);
    }
}
