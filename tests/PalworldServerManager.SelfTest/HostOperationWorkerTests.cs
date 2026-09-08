using PalworldServerManager.Core.Operations;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using static PalworldServerManager.SelfTest.OperationLifecycleTests;

namespace PalworldServerManager.SelfTest;

internal static class HostOperationWorkerTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(15));
    private static OperationTarget Target(PeerTrustTests.Fixture f) => new ServerTarget(new(f.HostId, Guid.NewGuid()));
    private static HostOperationRuntime Runtime(PeerTrustTests.Fixture f, Func<HostOperationExecution, CancellationToken, Task> work,
        LockRequirement requirement = LockRequirement.ServerExclusive)
        => new(f.Database, f.HostId, [new(Definition("WorkerFixture", requirement), work)], f.Time);
    private static DurableOperation Start(HostOperationRuntime runtime, PeerTrustTests.Fixture f, CancellationToken ct = default,
        Guid? id = null, OperationTarget? target = null)
        => runtime.Start(id ?? Guid.NewGuid(), "WorkerFixture", target ?? Target(f), Admit, ct);
    private static async Task Refuse<T>(Func<Task> action) where T : Exception
    { try { await Wait(action()); } catch (T) { return; } throw new Exception("Expected worker refusal: " + typeof(T).Name); }

    public static async Task DisconnectAndWaitCancellationDoNotCancelWork()
    {
        using var f = new PeerTrustTests.Fixture(); var entered = Signal(); var release = Signal();
        HostOperationExecution? held = null; CancellationToken workerToken = default;
        await using var runtime = Runtime(f, async (execution, stop) =>
        {
            held = execution; workerToken = stop; entered.SetResult(); await release.Task;
            execution.Transition("Done", Admit);
        });
        try
        {
            using var client = new CancellationTokenSource(); var op = Start(runtime, f, client.Token);
            await Wait(entered.Task); client.Cancel(); Check(!workerToken.IsCancellationRequested);
            await Refuse<OperationCanceledException>(() => runtime.WaitForCurrentWorkersAsync(client.Token));
            var first = runtime.Read(); var second = new OperationRepository(f.Database, f.HostId, [Definition("WorkerFixture", LockRequirement.ServerExclusive)], f.Time).Read();
            Check(first.Operations.Single().Status == HostOperationStatus.Running && first.DurableState.Locks.SequenceEqual(second.Locks));
            Check(second.Locks.Single().OwningOperationId == op.OperationId);
            release.SetResult(); await Wait(runtime.WaitForCurrentWorkersAsync());
            Check(runtime.Read().Operations.Single().Status == HostOperationStatus.Resolved && runtime.Read().DurableState.Locks.Count == 0);
            Reject<ObjectDisposedException>(() => held!.Heartbeat(Admit));
        }
        finally { release.TrySetResult(); }
    }

    public static async Task ShutdownDrainsWorkersAndRetainsUnfinishedState()
    {
        using var f = new PeerTrustTests.Fixture(); var entered = Signal(); var canceled = Signal(); var release = Signal();
        HostOperationExecution? held = null;
        await using var runtime = Runtime(f, async (execution, stop) =>
        {
            held = execution; using var registration = stop.Register(() => canceled.TrySetResult());
            entered.SetResult(); await release.Task;
        });
        try
        {
            var op = Start(runtime, f); await Wait(entered.Task);
            var shutdown = runtime.DisposeAsync().AsTask(); await Wait(canceled.Task);
            Check(!shutdown.IsCompleted); Reject<ObjectDisposedException>(() => Start(runtime, f));
            Reject<OperationCanceledException>(() => held!.Heartbeat(Admit));
            Check(runtime.Read().DurableState.Locks.Single().OwningOperationId == op.OperationId);
            release.SetResult(); await Wait(shutdown); await runtime.DisposeAsync();
            Check(runtime.Read().Operations.Single().Status == HostOperationStatus.RecoveryRequired);
            var calls = 0;
            await using var reopened = Runtime(f, (_, _) => { calls++; return Task.CompletedTask; });
            Check(reopened.Read().Operations.Single().Status == HostOperationStatus.AwaitingRecovery && calls == 0);
            Check(reopened.Read().DurableState.Locks.Single().OwningOperationId == op.OperationId);
        }
        finally { release.TrySetResult(); }
    }

    public static async Task FailedAndUnfinishedWorkersNeverReleaseLocks()
    {
        using var f = new PeerTrustTests.Fixture();
        var definitions = new[] { Definition("ThrowFixture", LockRequirement.ServerExclusive), Definition("ReturnFixture", LockRequirement.ServerExclusive) };
        await using var runtime = new HostOperationRuntime(f.Database, f.HostId,
            [new(definitions[0], (_, _) => throw new InvalidOperationException("fixture-private-exception")),
             new(definitions[1], (_, _) => Task.CompletedTask)], f.Time);
        foreach (var definition in definitions) runtime.Start(Guid.NewGuid(), definition.Kind, Target(f), Admit);
        await Wait(runtime.WaitForCurrentWorkersAsync()); var state = runtime.Read();
        Check(state.Operations.Count == 2 && state.Operations.All(o => o.Status == HostOperationStatus.RecoveryRequired && !o.Operation.IsTerminal));
        Check(state.DurableState.Locks.Count == 2 && state.DurableState.Operations.All(o => o.Operation.Revision == 1));
        // Public observations have no raw failure/exception message field.
        Check(!System.Text.Json.JsonSerializer.Serialize(state).Contains("fixture-private-exception", StringComparison.Ordinal));
    }

    public static async Task FailedAndContendingAdmissionsDispatchExactlyOnce()
    {
        using var f = new PeerTrustTests.Fixture(); var entered = Signal(); var release = Signal(); var calls = 0;
        await using var runtime = Runtime(f, async (execution, _) =>
        { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; execution.Transition("Done", Admit); });
        try
        {
            var id = Guid.NewGuid(); var target = Target(f);
            Reject<UnauthorizedAccessException>(() => runtime.Start(id, "WorkerFixture", target, (_, _) => throw new UnauthorizedAccessException()));
            Reject<OperationCanceledException>(() => Start(runtime, f, new CancellationToken(true), id, target));
            Reject<ArgumentException>(() => runtime.Start(id, "Unknown", target, Admit));
            Check(runtime.Read().Operations.Count == 0 && calls == 0);
            var accepted = 0; var conflicts = 0;
            Parallel.For(0, 8, _ =>
            {
                try { Start(runtime, f, id: id, target: target); Interlocked.Increment(ref accepted); }
                catch (OperationConflictException) { Interlocked.Increment(ref conflicts); }
            });
            await Wait(entered.Task); Check(accepted == 1 && conflicts == 7 && calls == 1);
            Check(runtime.Read().DurableState.Locks.Count == 1);
            release.SetResult(); await Wait(runtime.WaitForCurrentWorkersAsync());
            Reject<OperationConflictException>(() => Start(runtime, f, id: id, target: target)); Check(calls == 1);
        }
        finally { release.TrySetResult(); }
    }

    public static async Task WorkerDoesNotInheritAmbientRequestContext()
    {
        using var f = new PeerTrustTests.Fixture(); var ambient = new AsyncLocal<string?>(); ambient.Value = "fixture-request-context";
        string? observed = "unset";
        await using var runtime = Runtime(f, (execution, _) =>
        { observed = ambient.Value; execution.Transition("Done", Admit); return Task.CompletedTask; });
        Start(runtime, f); await Wait(runtime.WaitForCurrentWorkersAsync());
        Check(observed is null && ambient.Value == "fixture-request-context"); ambient.Value = null;
        Check(runtime.Read().Operations.Single().Status == HostOperationStatus.Resolved);
    }

    public static async Task StartupInspectsEveryTargetWithoutImplicitDispatch()
    {
        using var f = new PeerTrustTests.Fixture(); var calls = 0;
        var definitions = Enum.GetValues<RecoveryDisposition>().Select(d => new OperationDefinition(d.ToString(), LockRequirement.None,
            "Start", [OperationPhase.Active("Start", d, "Done"), OperationPhase.Terminal("Done")])).ToArray();
        var repository = new OperationRepository(f.Database, f.HostId, definitions, f.Time);
        foreach (var d in definitions)
        {
            repository.Start(Guid.NewGuid(), d.Kind, new HostTarget(f.HostId), Admit);
            repository.Start(Guid.NewGuid(), d.Kind, Target(f), Admit);
        }
        var before = repository.Read();
        await using (var runtime = new HostOperationRuntime(f.Database, f.HostId,
            definitions.Select(d => new HostOperationExecutor(d, (_, _) => { calls++; return Task.CompletedTask; })), f.Time))
        {
            var state = runtime.Read(); Check(calls == 0 && state.Operations.Count == 8);
            Check(state.Operations.All(o => o.Status == (o.Operation.Recovery == RecoveryDisposition.RequiresManualReview
                ? HostOperationStatus.RecoveryRequired : HostOperationStatus.AwaitingRecovery)));
            Check(state.DurableState.Revision == before.Revision && state.DurableState.Operations.SequenceEqual(before.Operations));
        }
        await using var missing = new HostOperationRuntime(f.Database, f.HostId, [], f.Time);
        Check(missing.Read().Operations.All(o => o.Status == HostOperationStatus.RecoveryRequired) && calls == 0);
        Reject<ArgumentException>(() => new HostOperationRuntime(f.Database, f.HostId,
            [new(definitions[0], (_, _) => Task.CompletedTask), new(definitions[0], (_, _) => Task.CompletedTask)], f.Time));
    }

    public static async Task StaleExternalTransitionDoesNotOverwriteOrRelease()
    {
        using var f = new PeerTrustTests.Fixture(); var entered = Signal(); var release = Signal();
        HostOperationExecution? held = null;
        await using var runtime = Runtime(f, async (execution, _) =>
        { held = execution; entered.SetResult(); await release.Task; execution.Transition("Done", Admit); });
        try
        {
            var op = Start(runtime, f); await Wait(entered.Task);
            var repository = new OperationRepository(f.Database, f.HostId, [Definition("WorkerFixture", LockRequirement.ServerExclusive)], f.Time);
            repository.Transition(op.OperationId, 1, "Work", Admit);
            release.SetResult(); await Wait(runtime.WaitForCurrentWorkersAsync()); var state = runtime.Read();
            Check(state.Operations.Single().Status == HostOperationStatus.RecoveryRequired && state.Operations.Single().Operation.Phase == "Work");
            Check(state.Operations.Single().Operation.Revision == 2 && state.DurableState.Locks.Count == 1);
            Reject<ObjectDisposedException>(() => held!.Transition("Done", Admit));
        }
        finally { release.TrySetResult(); }
    }

    public static async Task ThrowingShutdownCallbackStillDrains()
    {
        using var f = new PeerTrustTests.Fixture(); var entered = Signal(); var canceled = Signal(); var release = Signal();
        var runtime = Runtime(f, async (_, stop) =>
        {
            using var registration = stop.Register(() => { canceled.SetResult(); throw new InvalidOperationException("fixture-callback-secret"); });
            entered.SetResult(); await release.Task;
        });
        try
        {
            Start(runtime, f); await Wait(entered.Task); var shutdown = runtime.DisposeAsync().AsTask();
            await Wait(canceled.Task); Check(!shutdown.IsCompleted && runtime.Read().DurableState.Locks.Count == 1);
            release.SetResult(); await Refuse<InvalidOperationException>(() => shutdown);
            Check(runtime.Read().Operations.Single().Status == HostOperationStatus.RecoveryRequired);
            await Refuse<InvalidOperationException>(() => runtime.DisposeAsync().AsTask());
        }
        finally
        {
            release.TrySetResult();
            try { await Wait(runtime.DisposeAsync().AsTask()); } catch (InvalidOperationException) { }
        }
    }

    public static async Task ConcurrentShutdownCannotLoseAnAdmittedWorker()
    {
        using var f = new PeerTrustTests.Fixture(); var release = Signal(); var begin = Signal(); var calls = 0;
        var accepted = 0; var refused = 0;
        await using var runtime = Runtime(f, async (_, _) => { Interlocked.Increment(ref calls); await release.Task; });
        try
        {
            Start(runtime, f); // Non-vacuous drain even when shutdown wins all eight races.
            var admissions = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                await begin.Task;
                try { Start(runtime, f); Interlocked.Increment(ref accepted); }
                catch (ObjectDisposedException) { Interlocked.Increment(ref refused); }
            })).ToArray();
            Task? drain = null;
            var shutdownAdmission = Task.Run(async () => { await begin.Task; drain = runtime.DisposeAsync().AsTask(); });
            begin.SetResult(); await Wait(Task.WhenAll(admissions.Append(shutdownAdmission)));
            Check(accepted + refused == 8 && runtime.Read().Operations.Count == accepted + 1);
            Check(runtime.Read().DurableState.Locks.Count == accepted + 1 && !drain!.IsCompleted);
            release.SetResult(); await Wait(drain!);
            Check(calls == accepted + 1 && runtime.Read().DurableState.Locks.Count == accepted + 1);
            Check(runtime.Read().Operations.All(o => o.Status == HostOperationStatus.RecoveryRequired));
        }
        finally { release.TrySetResult(); }
    }
}
