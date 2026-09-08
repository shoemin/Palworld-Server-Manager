using PalworldServerManager.Host;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.SelfTest;

internal static class RecoveryContactCoordinatorTests
{
    private static void Check(bool value){if(!value)throw new Exception("Recovery contact coordinator assertion failed.");}
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static PeerRecoveryContact Contact(Guid? peer=null,string address="https://unused.invalid/")
        =>new(peer??Guid.NewGuid(),new(address),new(new string('A',64),new string('B',64)));
    public static async Task CoalescesLatestContactWithoutInlineOrAmbientWork()
    {
        var entered=Signal();var release=Signal();var local=new AsyncLocal<string?>();local.Value="user-context";
        var calls=0;string? observed="unset";var addresses=new List<Uri>();
        await using var coordinator=new PeerRecoveryContactCoordinator(Guid.NewGuid(),async(contact,ct)=>
        {
            observed=local.Value;addresses.Add(contact.Address);
            if(Interlocked.Increment(ref calls)==1){entered.TrySetResult();await release.Task.WaitAsync(ct);}
        });
        var first=Contact();var task=coordinator.Notify(first);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for(var i=0;i<100;i++)Check(ReferenceEquals(task,coordinator.Notify(first with{Address=new($"https://contact-{i}.invalid/")})));
            Check(!task.IsCompleted&&calls==1&&observed is null);release.TrySetResult();
            Check(await task.WaitAsync(TimeSpan.FromSeconds(5))==PeerRecoveryContactOutcome.AttemptFinished);
            Check(calls==2&&addresses[0]==first.Address&&addresses[1]==new Uri("https://contact-99.invalid/"));
            await Task.Delay(40);Check(calls==2); // No timer or autonomous follow-up.
        }
        finally{release.TrySetResult();await coordinator.StopAsync();local.Value=null;}
    }
    public static async Task CapacityAndStopRetainAllActualCleanup()
    {
        var entered=Signal();var cleanup=Signal();var release=Signal();var active=0;var cleaning=0;
        await using var coordinator=new PeerRecoveryContactCoordinator(Guid.NewGuid(),async(contact,ct)=>
        {
            if(Interlocked.Increment(ref active)==PeerRecoveryContactCoordinator.MaximumWorkers)entered.TrySetResult();
            try{await Task.Delay(Timeout.Infinite,ct);}
            finally{if(Interlocked.Increment(ref cleaning)==PeerRecoveryContactCoordinator.MaximumWorkers)cleanup.TrySetResult();await release.Task;Interlocked.Decrement(ref active);}
        });
        var tasks=Enumerable.Range(0,PeerRecoveryContactCoordinator.MaximumWorkers).Select(_=>coordinator.Notify(Contact())).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(await coordinator.Notify(Contact())==PeerRecoveryContactOutcome.NotScheduled);
            var stop=coordinator.StopAsync();await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!stop.IsCompleted&&active==32&&ReferenceEquals(stop,coordinator.StopAsync()));
            Check(await coordinator.Notify(Contact())==PeerRecoveryContactOutcome.NotScheduled);
        }
        finally{release.TrySetResult();await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));}
        Check(active==0&&(await Task.WhenAll(tasks)).All(x=>x==PeerRecoveryContactOutcome.AttemptFinished));
    }
    private sealed class PulseClock:TimeProvider
    {
        internal Timer? Current;
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)=>Current=new(callback,state,dueTime);
        internal sealed class Timer(TimerCallback callback,object? state,TimeSpan due):ITimer
        {
            internal bool Disposed;internal TimeSpan Due=>due;
            internal void Fire(){if(!Disposed)callback(state);}
            public bool Change(TimeSpan dueTime,TimeSpan period)=>!Disposed;
            public void Dispose()=>Disposed=true;
            public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
        }
    }
    public static async Task OneDeadlineCancelsPendingFollowupAndDrains()
    {
        var clock=new PulseClock();var entered=Signal();var cleaned=false;var calls=0;
        await using var coordinator=new PeerRecoveryContactCoordinator(Guid.NewGuid(),async(contact,ct)=>
        {Interlocked.Increment(ref calls);entered.TrySetResult();try{await Task.Delay(Timeout.Infinite,ct);}finally{cleaned=true;}},clock);
        var contact=Contact();var task=coordinator.Notify(contact);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(ReferenceEquals(task,coordinator.Notify(contact))&&clock.Current!.Due==TimeSpan.FromSeconds(30));clock.Current!.Fire();
        Check(await task.WaitAsync(TimeSpan.FromSeconds(5))==PeerRecoveryContactOutcome.AttemptFinished&&calls==1&&cleaned&&clock.Current.Disposed);
        var invalid=new[]{contact with{Peer=Guid.Empty},contact with{Address=new("http://unused.invalid/")},contact with{Address=new("https://user@unused.invalid/")},contact with{Address=new("https://unused.invalid/path")}};
        foreach(var bad in invalid){try{_=coordinator.Notify(bad);throw new Exception("Invalid contact accepted.");}catch(ArgumentException){}}
    }
    public static async Task StopCallbackFailureStillDrains()
    {
        var entered=Signal();var cleaned=false;
        var coordinator=new PeerRecoveryContactCoordinator(Guid.NewGuid(),async(contact,ct)=>
        {
            using var registration=ct.Register(()=>throw new InvalidOperationException("fixture cancellation callback"));
            entered.TrySetResult();try{await Task.Delay(Timeout.Infinite,ct);}finally{cleaned=true;}
        });
        var task=coordinator.Notify(Contact());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var failed=false;try{await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch(AggregateException){failed=true;}
            Check(failed&&cleaned&&await task==PeerRecoveryContactOutcome.AttemptFinished);
        }
        finally
        {
            try{await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch(AggregateException){}
        }
    }
}
