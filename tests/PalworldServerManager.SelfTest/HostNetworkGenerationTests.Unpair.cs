using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using Fixture=PalworldServerManager.SelfTest.PeerSecurityRpcTests.Fixture;

namespace PalworldServerManager.SelfTest;

internal static partial class HostNetworkGenerationTests
{
    public static async Task RegisteredUnpairPreparationStopsWithGeneration()
    {
        await using var a=new Fixture();await using var b=new Fixture();var clock=new Clock();
        await using var generation=await Start(a,Pipe(),clock);a.Bind(b);b.Bind(a);await b.Start();
        await generation.ActivateAsync(b.State.HostId,b.Address);
        var coordinator=generation.UnpairNotifications!;
        Check(await coordinator.Prepare(b.State.HostId,b.Address));
        await Bounded(generation.StopAsync());
        Check(clock.Active==0&&a.Certificate.Value.Handle==IntPtr.Zero&&a.State.Repository.Read(b.State.HostId)!.State=="Active");
        Check(await coordinator.Notify(new(b.State.HostId,0,2,true,0,0,1))==PeerUnpairNotification.NotAttempted);
        await Reject<InvalidOperationException>(()=>coordinator.Prepare(b.State.HostId,b.Address));
    }
    public static async Task PreparedUnpairConnectionBelongsToGeneration()
    {
        await using var a=new Fixture();await using var b=new Fixture();var clock=new Clock();
        await using var generation=await Start(a,Pipe(),clock);a.Bind(b);b.Bind(a);await b.Start();
        await generation.ActivateAsync(b.State.HostId,b.Address);
        var ready=Signal();PeerUnpairConnection? captured=null;
        var work=generation.WithPeerUnpairConnectionAsync(b.State.HostId,b.Address,async(held,ct)=>
        {captured=held;ready.TrySetResult();await Task.Delay(Timeout.Infinite,ct);return true;});
        await Bounded(ready.Task);var revoked=LiveUnpairConnectionTests.Revoke(a,b);
        await Bounded(generation.StopAsync());await Reject<OperationCanceledException>(()=>work);
        Check(clock.Active==0&&a.Certificate.Value.Handle==IntPtr.Zero);
        Check(await captured!.Send(revoked)==PeerUnpairDelivery.Unconfirmed&&b.State.Repository.Read(a.State.HostId)!.State=="Active");
        await Reject<InvalidOperationException>(()=>generation.WithPeerUnpairConnectionAsync(b.State.HostId,b.Address,(held,ct)=>Task.FromResult(true)));
    }
}
