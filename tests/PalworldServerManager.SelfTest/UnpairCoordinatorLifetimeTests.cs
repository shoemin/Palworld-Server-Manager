using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static class UnpairCoordinatorLifetimeTests
{
    private static void Check(bool value){if(!value)throw new Exception("Unpair coordinator lifetime assertion failed.");}
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static async Task CapacityAndRemovedWorkersDrainActualCleanup()
    {
        var entered=Signal();var cleanup=Signal();var release=Signal();var active=0;var cleaning=0;
        await using var coordinator=new PeerUnpairCoordinator(Guid.NewGuid(),async(peer,address,work,ct)=>
        {
            if(Interlocked.Increment(ref active)==PeerUnpairCoordinator.MaximumWorkers)entered.TrySetResult();
            try{await Task.Delay(Timeout.Infinite,ct);return PeerUnpairNotification.NotAttempted;}
            finally
            {
                if(Interlocked.Increment(ref cleaning)==PeerUnpairCoordinator.MaximumWorkers)cleanup.TrySetResult();
                await release.Task;Interlocked.Decrement(ref active);
            }
        });
        var peers=Enumerable.Range(0,PeerUnpairCoordinator.MaximumWorkers).Select(_=>Guid.NewGuid()).ToArray();
        var readiness=peers.Select(p=>coordinator.Prepare(p,new("https://unused.invalid/"))).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!await coordinator.Prepare(Guid.NewGuid(),new("https://unused.invalid/"))&&!await coordinator.Prepare(peers[0],new("https://unused.invalid/")));
            Check(await coordinator.Notify(new(peers[0],0,2,true,0,0,1))==PeerUnpairNotification.NotAttempted);
            var stop=coordinator.StopAsync();await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!stop.IsCompleted&&active==PeerUnpairCoordinator.MaximumWorkers&&ReferenceEquals(stop,coordinator.StopAsync()));
        }
        finally{release.TrySetResult();await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));}
        Check(active==0&&(await Task.WhenAll(readiness)).All(v=>!v));
        try{_=coordinator.Prepare(Guid.NewGuid(),new("https://unused.invalid/"));throw new Exception("Closed coordinator admitted work.");}
        catch(InvalidOperationException){}
    }
    private sealed class PulseClock:TimeProvider
    {
        internal Timer? Current;
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
            =>Current=new Timer(callback,state,dueTime);
        internal sealed class Timer(TimerCallback callback,object? state,TimeSpan due):ITimer
        {
            internal bool Disposed;internal TimeSpan Due=>due;
            internal void Fire(){if(!Disposed)callback(state);}
            public bool Change(TimeSpan dueTime,TimeSpan period)=>!Disposed;
            public void Dispose()=>Disposed=true;
            public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
        }
    }
    public static async Task RetentionExpiryClosesPreparationWithoutNotice()
    {
        var clock=new PulseClock();var entered=Signal();var exited=false;
        await using var coordinator=new PeerUnpairCoordinator(Guid.NewGuid(),async(peer,address,work,ct)=>
        {entered.TrySetResult();try{await Task.Delay(Timeout.Infinite,ct);return PeerUnpairNotification.NotAttempted;}finally{exited=true;}},clock);
        var ready=coordinator.Prepare(Guid.NewGuid(),new("https://unused.invalid/"));await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(clock.Current!.Due==PeerUnpairCoordinator.MaximumLifetime);clock.Current.Fire();
        Check(!await ready.WaitAsync(TimeSpan.FromSeconds(5)));await coordinator.StopAsync();Check(exited&&clock.Current.Disposed);
    }
    public static async Task StopCallbackFailureStillDrainsWorkers()
    {
        var entered=Signal();var exited=false;
        var coordinator=new PeerUnpairCoordinator(Guid.NewGuid(),async(peer,address,work,ct)=>
        {
            using var fault=ct.Register(()=>throw new InvalidOperationException("fixture cancellation callback failure"));
            entered.TrySetResult();try{await Task.Delay(Timeout.Infinite,ct);return PeerUnpairNotification.NotAttempted;}finally{exited=true;}
        });
        var ready=coordinator.Prepare(Guid.NewGuid(),new("https://unused.invalid/"));await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failed=false;try{await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch(AggregateException){failed=true;}
        Check(failed&&exited&&!await ready);
    }
}
