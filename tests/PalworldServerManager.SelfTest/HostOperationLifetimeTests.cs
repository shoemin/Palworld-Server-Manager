using PalworldServerManager.Core.Operations;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using static PalworldServerManager.SelfTest.OperationLifecycleTests;

namespace PalworldServerManager.SelfTest;

internal static class HostOperationLifetimeTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(15));

    public static async Task BootstrapThenConcurrentReadyUsesOneRuntime()
    {
        using var f = new PeerTrustTests.Fixture();
        f.Execute("UPDATE HostIdentity SET HostBootstrapState='Uninitialized'; DELETE FROM LocalPrincipals;");
        var definition = Definition("Fixture", LockRequirement.ServerExclusive); var recovered = 0; var ordinary = 0;
        var registrations = new List<HostOperationExecutor> { new(definition,
            (execution, _) => { Interlocked.Increment(ref ordinary); execution.Transition("Done", Admit); return Task.CompletedTask; },
            [new(RecoveryDisposition.SafeToRetryFromStart, HostOperationRecoveryTests.RecoveryAdmit,
                (execution, _) => { Interlocked.Increment(ref recovered); execution.Transition("Done", Admit); return Task.CompletedTask; })]) };
        await using var owner = new HostOperationLifetime(f.Database, f.HostId, registrations); registrations.Clear();
        Check(!owner.InitializeIfReady()); Reject<InvalidOperationException>(() => owner.GetReady());
        Check(f.Count("OperationRecords") == 0 && f.Count("LocalPrincipals") == 0);
        // Canonical persisted initialization, representative of the completed bootstrap
        // transaction; the actual credential ceremony remains independently qualified.
        using (var tx = f.Writer.BeginTransaction())
        { new HostIdentityRepository(f.Database).InitializeWithOwner(f.Writer, tx, Guid.NewGuid().ToString("D"), "new-native", "new-public"); tx.Commit(); }
        var interrupted = new OperationRepository(f.Database, f.HostId, [definition]).Start(Guid.NewGuid(), definition.Kind,
            new ServerTarget(new(f.HostId, f.PeerId)), Admit);
        var readers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(owner.GetReady)));
        Check(readers.All(r => ReferenceEquals(r, readers[0]))); var runtime = readers[0]; await Wait(runtime.WaitForCurrentWorkersAsync());
        Check(recovered == 1 && ordinary == 0 && owner.InitializeIfReady());
        Check(runtime.Read().Operations.Single().Operation.OperationId == interrupted.OperationId);
        runtime.Start(Guid.NewGuid(), definition.Kind, new ServerTarget(new(f.HostId, f.PeerId)), Admit);
        await Wait(runtime.WaitForCurrentWorkersAsync()); Check(ordinary == 1 && recovered == 1);
    }

    public static async Task EmptyProductionRegistryPreservesEveryTarget()
    {
        using var f = new PeerTrustTests.Fixture(); var repository = new OperationRepository(f.Database, f.HostId, Definitions());
        repository.Start(Guid.NewGuid(), "ReadFixture", new HostTarget(f.HostId), Admit);
        repository.Start(Guid.NewGuid(), "ServerFixture", new ServerTarget(new(f.HostId, f.PeerId)), Admit);
        var before = repository.Read();
        await using var owner = new HostOperationLifetime(f.Database, f.HostId, []);
        Check(owner.InitializeIfReady()); var runtime = owner.GetReady(); var after = runtime.Read();
        Check(after.DurableState.Revision == before.Revision && after.DurableState.Locks.SequenceEqual(before.Locks));
        Check(after.Operations.Count == 2 && after.Operations.All(o => o.Status == HostOperationStatus.RecoveryRequired));
        Check(after.Operations.Select(o => o.Operation).SequenceEqual(before.Operations.Select(o => o.Operation)));
        await Wait(runtime.WaitForCurrentWorkersAsync()); Check(runtime.Read().DurableState.Revision == before.Revision);
        Reject<ArgumentException>(() => runtime.Start(Guid.NewGuid(), "ReadFixture", new HostTarget(f.HostId), Admit));
    }

    public static async Task FailedInitializationNeverRetriesInSameOwner()
    {
        using var f = new PeerTrustTests.Fixture();
        f.Execute("DELETE FROM OperationStateRevision;");
        await using var owner = new HostOperationLifetime(f.Database, f.HostId, []);
        Reject<InvalidDataException>(() => owner.InitializeIfReady());
        // Repairing the disposable fixture does not silently restart a failed owner.
        f.Execute("INSERT INTO OperationStateRevision VALUES (1,0);");
        Reject<InvalidOperationException>(() => owner.GetReady());
        await owner.DisposeAsync(); Reject<ObjectDisposedException>(() => owner.InitializeIfReady());
        await using var fresh = new HostOperationLifetime(f.Database, f.HostId, []); Check(fresh.InitializeIfReady());
        Reject<ArgumentException>(() => new HostOperationLifetime(f.Database, Guid.Empty, []));
        var d = Definition("Fixture", LockRequirement.None); var e = new HostOperationExecutor(d, (_, _) => Task.CompletedTask);
        Reject<ArgumentException>(() => new HostOperationLifetime(f.Database, f.HostId, [e, e]));
    }

    public static async Task ShutdownRacingReadyDrainsActualWork()
    {
        using var f = new PeerTrustTests.Fixture(); var entered = Signal(); var canceled = Signal(); var release = Signal();
        var definition = Definition("Fixture", LockRequirement.HostExclusive);
        await using var owner = new HostOperationLifetime(f.Database, f.HostId,
            [new(definition, async (_, stop) => { using var registration = stop.Register(() => canceled.TrySetResult()); entered.SetResult(); await release.Task; })]);
        var runtime = owner.GetReady(); runtime.Start(Guid.NewGuid(), definition.Kind, new HostTarget(f.HostId), Admit);
        try
        {
            await Wait(entered.Task); var go = Signal();
            var accesses = Enumerable.Range(0, 8).Select(_ => Task.Run(async () => {
                await go.Task; try { Check(ReferenceEquals(owner.GetReady(), runtime)); } catch (ObjectDisposedException) { } })).ToArray();
            var stopping = Task.Run(async () => { await go.Task; await owner.DisposeAsync(); }); go.SetResult();
            await Wait(canceled.Task); await Wait(Task.WhenAll(accesses)); Check(!stopping.IsCompleted);
            Reject<ObjectDisposedException>(() => owner.GetReady());
            Reject<ObjectDisposedException>(() => runtime.Start(Guid.NewGuid(), definition.Kind, new HostTarget(f.HostId), Admit));
            Check(runtime.Read().DurableState.Locks.Count == 1);
            release.SetResult(); await Wait(stopping); await owner.DisposeAsync();
            Check(runtime.Read().Operations.Single().Status == HostOperationStatus.RecoveryRequired);
        }
        finally { release.TrySetResult(); }
    }

    public static async Task EnclosingLeaseOutlivesWorkerDrain()
    {
        using var f = new PeerTrustTests.Fixture(); var entered = Signal(); var canceled = Signal(); var release = Signal();
        var hostStop = Signal(); var leaseHeld = Signal(); var mutex = @"Global\PSMOperationOwner" + Guid.NewGuid().ToString("N");
        var definition = Definition("Fixture", LockRequirement.None);
        // Same ownership order as installed composition, with an independent actual lease.
        var host = Task.Run(async () => {
            using var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero, mutex) ?? throw new InvalidOperationException();
            await using var owner = new HostOperationLifetime(f.Database, f.HostId,
                [new(definition, async (_, stop) => { using var r = stop.Register(() => canceled.TrySetResult()); entered.SetResult(); await release.Task; })]);
            owner.GetReady().Start(Guid.NewGuid(), definition.Kind, new HostTarget(f.HostId), Admit);
            leaseHeld.SetResult(); await hostStop.Task;
            await owner.DisposeAsync();
        });
        try
        {
            await Wait(leaseHeld.Task); await Wait(entered.Task); hostStop.SetResult(); await Wait(canceled.Task);
            using (var denied = HostExclusivityLock.TryAcquire(TimeSpan.Zero, mutex)) Check(denied is null);
            Check(!host.IsCompleted); release.SetResult(); await Wait(host);
            using var acquired = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(5), mutex); Check(acquired is not null);
        }
        finally { hostStop.TrySetResult(); release.TrySetResult(); await Wait(host); }
    }
}
