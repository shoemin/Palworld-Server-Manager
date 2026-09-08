using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Operations;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using static PalworldServerManager.SelfTest.OperationLifecycleTests;

namespace PalworldServerManager.SelfTest;

internal static class HostOperationRecoveryTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(15));
    internal static readonly Action<DurableOperation, SqliteConnection, SqliteTransaction> RecoveryAdmit = (_, _, _) => { };
    private static OperationDefinition Policy(RecoveryDisposition disposition, LockRequirement scope = LockRequirement.ServerExclusive, int version = 1)
        => new(disposition.ToString(), scope, "Start",
            [OperationPhase.Active("Start", disposition, "Checkpoint", "Done", "Failed"),
             OperationPhase.Active("Checkpoint", disposition, "Done", "Failed"), OperationPhase.Terminal("Done"), OperationPhase.Terminal("Failed")], version);
    private static OperationTarget Target(PeerTrustTests.Fixture f, bool host = false)
        => host ? new HostTarget(f.HostId) : new ServerTarget(new(f.HostId, f.PeerId));
    private static OperationRepository Repository(PeerTrustTests.Fixture f, params OperationDefinition[] definitions)
        => new(f.Database, f.HostId, definitions, f.Time);
    private static DurableOperation Interrupted(PeerTrustTests.Fixture f, OperationDefinition definition, OperationTarget? target = null)
    {
        var repository = Repository(f, definition); var op = repository.Start(Guid.NewGuid(), definition.Kind, target ?? Target(f), Admit);
        return repository.Transition(op.OperationId, 1, "Checkpoint", Admit);
    }
    private static string Snapshot(PeerTrustTests.Fixture f)
    {
        var rows = new List<object?[]>();
        foreach (var table in new[] { "OperationRecords", "OperationLocks", "OperationStateRevision", "HostIdentity", "LocalPrincipals", "AuthorizationRevision" })
        {
            using var cmd = f.Writer.CreateCommand(); cmd.CommandText = "SELECT * FROM " + table + " ORDER BY 1;";
            using var r = cmd.ExecuteReader(); rows.Add([table]);
            while (r.Read()) rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : r.GetValue(i)).ToArray());
        }
        return JsonSerializer.Serialize(rows);
    }

    public static Task PreparationRequiresExactPolicyAndKeepsIdentityAndLock()
    {
        foreach (var d in new[] { RecoveryDisposition.SafeToRetryFromStart, RecoveryDisposition.SafeToResumeFromPhase, RecoveryDisposition.SafeToDiscard })
        foreach (var host in new[] { false, true })
        {
            using var f = new PeerTrustTests.Fixture(); var definition = Policy(d, host ? LockRequirement.HostExclusive : LockRequirement.ServerExclusive);
            var repository = Repository(f, definition); var op = Interrupted(f, definition, Target(f, host)); var before = Snapshot(f);
            var oldLock = repository.Read().Locks.Single();
            Reject<InvalidOperationException>(() => repository.PrepareRecovery(op.OperationId, 2,
                d == RecoveryDisposition.SafeToRetryFromStart ? RecoveryDisposition.SafeToResumeFromPhase : RecoveryDisposition.SafeToRetryFromStart, Admit));
            Reject<StaleWriteConflictException>(() => repository.PrepareRecovery(op.OperationId, 1, d, Admit));
            Reject<ArgumentException>(() => repository.PrepareRecovery(op.OperationId, 2, RecoveryDisposition.RequiresManualReview, Admit));
            Reject<ArgumentException>(() => repository.PrepareRecovery(op.OperationId, 2, (RecoveryDisposition)999, Admit));
            Check(Snapshot(f) == before);
            var prepared = repository.PrepareRecovery(op.OperationId, 2, d, Admit);
            Check(prepared.Revision == 3 && prepared.OperationId == op.OperationId && prepared.Target == op.Target && !prepared.IsTerminal);
            Check(prepared.Phase == (d == RecoveryDisposition.SafeToRetryFromStart ? "Start" : "Checkpoint"));
            Check(repository.Read().Locks.Single() == oldLock);
        }
        using var manual = new PeerTrustTests.Fixture(); var policy = Policy(RecoveryDisposition.RequiresManualReview);
        var held = Interrupted(manual, policy); var snapshot = Snapshot(manual);
        Reject<InvalidOperationException>(() => Repository(manual, policy).PrepareRecovery(held.OperationId, 2, RecoveryDisposition.SafeToRetryFromStart, Admit));
        Check(Snapshot(manual) == snapshot); return Task.CompletedTask;
    }

    public static Task PreparationFaultsRollBackAndRetry()
    {
        var faults = new[] {
            "DELETE FROM OperationRecords WHERE OperationId=NEW.OperationId;",
            "DELETE FROM OperationLocks;",
            "UPDATE OperationRecords SET RecordRevision=RecordRevision+1 WHERE OperationId=NEW.OperationId;",
            "UPDATE HostIdentity SET HostBootstrapState='Uninitialized';",
            "UPDATE OperationLocks SET AcquiredUtc='2026-01-01T00:00:00.0000000+00:00';",
            "DELETE FROM OperationStateRevision;"
        };
        foreach (var fault in faults)
        {
            using var f = new PeerTrustTests.Fixture(); var definition = Policy(RecoveryDisposition.SafeToRetryFromStart);
            var op = Interrupted(f, definition); var before = Snapshot(f); var repository = Repository(f, definition);
            f.Execute("CREATE TRIGGER RecoveryFault AFTER UPDATE ON OperationRecords BEGIN " + fault + " END;");
            Reject<Exception>(() => repository.PrepareRecovery(op.OperationId, 2, op.Recovery!.Value, Admit));
            Check(Snapshot(f) == before); f.Execute("DROP TRIGGER RecoveryFault;");
            Check(repository.PrepareRecovery(op.OperationId, 2, op.Recovery!.Value, Admit).Phase == "Start");
            Check(repository.Read().Locks.Count == 1);
        }
        foreach (var late in new[] { false, true }) foreach (var cancel in new[] { false, true })
        {
            using var f = new PeerTrustTests.Fixture(); var definition = Policy(RecoveryDisposition.SafeToRetryFromStart);
            var op = Interrupted(f, definition); var before = Snapshot(f); var repository = Repository(f, definition); var calls = 0;
            using var source = new CancellationTokenSource();
            void Authority(SqliteConnection _, SqliteTransaction __)
            { if (++calls != (late ? 2 : 1)) return; if (cancel) source.Cancel(); else throw new UnauthorizedAccessException(); }
            Reject<Exception>(() => repository.PrepareRecovery(op.OperationId, 2, op.Recovery!.Value, Authority, source.Token));
            Check(Snapshot(f) == before && repository.PrepareRecovery(op.OperationId, 2, op.Recovery!.Value, Admit).Revision == 3);
        }
        return Task.CompletedTask;
    }

    public static async Task StartupUsesOnlyExplicitHandlersOnBothTargets()
    {
        using var f = new PeerTrustTests.Fixture(); var ordinary = 0;
        var visited = new ConcurrentDictionary<Guid, DurableOperation>();
        var names = new Dictionary<RecoveryDisposition, string> {
            [RecoveryDisposition.SafeToRetryFromStart] = "Retry", [RecoveryDisposition.SafeToResumeFromPhase] = "Resume",
            [RecoveryDisposition.SafeToDiscard] = "Discard", [RecoveryDisposition.RequiresManualReview] = "Manual" };
        var phases = new List<OperationPhase> { OperationPhase.Active("Start", RecoveryDisposition.SafeToRetryFromStart,
            "Retry", "Resume", "Discard", "Manual", "Done", "Failed"), OperationPhase.Terminal("Done"), OperationPhase.Terminal("Failed") };
        phases.AddRange(names.Select(p => OperationPhase.Active(p.Value, p.Key, "Done", "Failed")));
        var definition = new OperationDefinition("MixedFixture", LockRequirement.None, "Start", phases);
        var repository = Repository(f, definition);
        foreach (var phase in names.Values) foreach (var host in new[] { false, true })
        {
            var op = repository.Start(Guid.NewGuid(), definition.Kind, Target(f, host), Admit);
            repository.Transition(op.OperationId, 1, phase, Admit);
        }
        var handlers = names.Keys.Where(d => d != RecoveryDisposition.RequiresManualReview).Select(d => new HostOperationRecoveryHandler(d,
            (operation, _, _) => Check(operation.Kind == "MixedFixture" && operation.Recovery == d),
            (execution, _) =>
            {
                visited[execution.Current.OperationId] = execution.Current;
                execution.Transition(d == RecoveryDisposition.SafeToDiscard ? "Failed" : "Done", Admit); return Task.CompletedTask;
            }));
        var registration = new HostOperationExecutor(definition, (_, _) => { Interlocked.Increment(ref ordinary); return Task.CompletedTask; }, handlers);
        await using var runtime = new HostOperationRuntime(f.Database, f.HostId, [registration], f.Time);
        runtime.ApplyStartupRecovery(); runtime.ApplyStartupRecovery(); await Wait(runtime.WaitForCurrentWorkersAsync());
        var state = runtime.Read(); Check(ordinary == 0 && visited.Count == 6 && state.Operations.Count == 8);
        foreach (var op in visited.Values)
        {
            Check(op.Revision == 3 && op.Target.AuthoritativeHostId == f.HostId);
            Check(op.Kind == "MixedFixture" && op.Phase == (op.Recovery == RecoveryDisposition.SafeToRetryFromStart ? "Start" : names[op.Recovery!.Value]));
        }
        Check(visited.Values.Count(o => o.Target is HostTarget) == 3 && visited.Values.Count(o => o.Target is ServerTarget) == 3);
        Check(state.Operations.Count(o => o.Status == HostOperationStatus.Resolved && o.Operation.Revision == 4) == 6);
        Check(state.Operations.Count(o => o.Status == HostOperationStatus.RecoveryRequired && o.Operation.Revision == 2) == 2);
    }

    public static async Task DiscardCleansBeforeReleasingEitherScope()
    {
        foreach (var host in new[] { false, true })
        {
            using var f = new PeerTrustTests.Fixture(); var definition = Policy(RecoveryDisposition.SafeToDiscard, host ? LockRequirement.HostExclusive : LockRequirement.ServerExclusive);
            var op = Interrupted(f, definition, Target(f, host)); var entered = Signal(); var release = Signal();
            var artifact = Path.Combine(Path.GetDirectoryName(f.Database.DatabasePath)!, "recovery-fixture.tmp"); File.WriteAllText(artifact, "partial fixture");
            await using var runtime = new HostOperationRuntime(f.Database, f.HostId,
                [new(definition, (_, _) => throw new Exception("Ordinary executor must not run."),
                    [new(RecoveryDisposition.SafeToDiscard, RecoveryAdmit, async (execution, _) =>
                    { entered.SetResult(); await release.Task; File.Delete(artifact); execution.Transition("Failed", Admit); })])], f.Time);
            try
            {
                runtime.ApplyStartupRecovery(); await Wait(entered.Task);
                Check(File.Exists(artifact) && runtime.Read().DurableState.Locks.Single().OwningOperationId == op.OperationId);
                Reject<OperationConflictException>(() => runtime.Start(Guid.NewGuid(), definition.Kind, op.Target, Admit));
                release.SetResult(); await Wait(runtime.WaitForCurrentWorkersAsync());
                Check(!File.Exists(artifact) && runtime.Read().DurableState.Locks.Count == 0);
                Check(runtime.Read().Operations.Single().Operation.Phase == "Failed");
            }
            finally { release.TrySetResult(); }
        }
    }

    public static async Task MissingChangedManualAndDamagedStateNeverDispatch()
    {
        foreach (var mode in new[] { "missing", "changed", "manual", "damaged" })
        {
            using var f = new PeerTrustTests.Fixture(); var calls = 0;
            var d = mode == "manual" ? RecoveryDisposition.RequiresManualReview : RecoveryDisposition.SafeToRetryFromStart;
            var definition = Policy(d); Interrupted(f, definition);
            if (mode == "damaged") f.Execute("UPDATE OperationRecords SET IsTerminal=1,Phase='Done',RecoveryDisposition=NULL,RecordRevision=RecordRevision+1;");
            var before = Snapshot(f); var handlers = d == RecoveryDisposition.RequiresManualReview || mode == "missing"
                ? Array.Empty<HostOperationRecoveryHandler>() : [new HostOperationRecoveryHandler(d, RecoveryAdmit, (_, _) => { calls++; return Task.CompletedTask; })];
            var registration = new HostOperationExecutor(mode == "changed" ? Policy(d, version: 2) : definition,
                (_, _) => { calls++; return Task.CompletedTask; }, handlers);
            await using var runtime = new HostOperationRuntime(f.Database, f.HostId, [registration], f.Time);
            runtime.ApplyStartupRecovery(); await Wait(runtime.WaitForCurrentWorkersAsync());
            Check(calls == 0 && Snapshot(f) == before && runtime.Read().Operations.Single().Status == HostOperationStatus.RecoveryRequired);
            Check(runtime.Read().DurableState.Locks.Count == 1);
        }
        Reject<ArgumentException>(() => new HostOperationRecoveryHandler(RecoveryDisposition.RequiresManualReview, RecoveryAdmit, (_, _) => Task.CompletedTask));
        Reject<ArgumentException>(() => new HostOperationRecoveryHandler((RecoveryDisposition)0, RecoveryAdmit, (_, _) => Task.CompletedTask));
        var handler = new HostOperationRecoveryHandler(RecoveryDisposition.SafeToRetryFromStart, RecoveryAdmit, (_, _) => Task.CompletedTask);
        Reject<ArgumentException>(() => new HostOperationExecutor(Policy(RecoveryDisposition.SafeToRetryFromStart), (_, _) => Task.CompletedTask, [handler, handler]));
        Reject<ArgumentException>(() => new HostOperationExecutor(Policy(RecoveryDisposition.SafeToDiscard), (_, _) => Task.CompletedTask, [handler]));
        var list = new List<HostOperationRecoveryHandler> { handler };
        var frozen = new HostOperationExecutor(Policy(RecoveryDisposition.SafeToRetryFromStart), (_, _) => Task.CompletedTask, list); list.Clear();
        Check(frozen.RecoveryHandlers.Count == 1);
    }

    public static async Task ConcurrentInitializationAndFailureNeverLoop()
    {
        using var f = new PeerTrustTests.Fixture(); var definition = Policy(RecoveryDisposition.SafeToRetryFromStart);
        var op = Interrupted(f, definition); var entered = Signal(); var release = Signal(); var calls = 0;
        await using var runtime = new HostOperationRuntime(f.Database, f.HostId,
            [new(definition, (_, _) => throw new Exception("Ordinary fallback forbidden."),
                [new(RecoveryDisposition.SafeToRetryFromStart, RecoveryAdmit, async (_, _) =>
                { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; throw new Exception("fixture-recovery-failure"); })])], f.Time);
        try
        {
            var before = Snapshot(f); Reject<InvalidOperationException>(() => runtime.Start(Guid.NewGuid(), definition.Kind, Target(f), Admit)); Check(Snapshot(f) == before);
            await Wait(Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(runtime.ApplyStartupRecovery)))); await Wait(entered.Task);
            Check(calls == 1 && runtime.Read().Operations.Single().Operation.Revision == 3);
            runtime.ApplyStartupRecovery(); release.SetResult(); await Wait(runtime.WaitForCurrentWorkersAsync()); runtime.ApplyStartupRecovery();
            var state = runtime.Read(); Check(calls == 1 && state.Operations.Single().Status == HostOperationStatus.RecoveryRequired);
            Check(state.Operations.Single().Operation.Phase == "Start" && state.Operations.Single().Operation.Revision == 3);
            Check(state.DurableState.Locks.Single().OwningOperationId == op.OperationId);
        }
        finally { release.TrySetResult(); }
    }

    public static async Task RecoveryShutdownDrainsAndKeepsPreparedState()
    {
        using var f = new PeerTrustTests.Fixture(); var definition = Policy(RecoveryDisposition.SafeToResumeFromPhase);
        var op = Interrupted(f, definition); var entered = Signal(); var canceled = Signal(); var release = Signal();
        await using var runtime = new HostOperationRuntime(f.Database, f.HostId,
            [new(definition, (_, _) => throw new Exception("Ordinary fallback forbidden."),
                [new(RecoveryDisposition.SafeToResumeFromPhase, RecoveryAdmit, async (execution, stop) =>
                { using var registration = stop.Register(() => canceled.TrySetResult()); entered.SetResult(); await release.Task; execution.Transition("Done", Admit); })])], f.Time);
        try
        {
            runtime.ApplyStartupRecovery(); await Wait(entered.Task); var shutdown = runtime.DisposeAsync().AsTask(); await Wait(canceled.Task);
            Check(!shutdown.IsCompleted && runtime.Read().DurableState.Locks.Single().OwningOperationId == op.OperationId);
            release.SetResult(); await Wait(shutdown);
            var state = runtime.Read(); Check(state.Operations.Single().Status == HostOperationStatus.RecoveryRequired);
            Check(state.Operations.Single().Operation.Phase == "Checkpoint" && state.Operations.Single().Operation.Revision == 3);
            Check(state.DurableState.Locks.Count == 1);
        }
        finally { release.TrySetResult(); }
    }

    public static async Task LateAuthorityRefusalSkipsOnlyThatWorker()
    {
        using var f = new PeerTrustTests.Fixture(); var definition = Policy(RecoveryDisposition.SafeToResumeFromPhase);
        var denied = Interrupted(f, definition); var allowed = Interrupted(f, definition, new ServerTarget(new(f.HostId, Guid.NewGuid())));
        var repository = Repository(f, definition); var before = repository.Read();
        var deniedLock = before.Locks.Single(l => l.OwningOperationId == denied.OperationId);
        var visited = new ConcurrentBag<Guid>(); var checks = 0;
        await using var runtime = new HostOperationRuntime(f.Database, f.HostId,
            [new(definition, (_, _) => throw new Exception("No ordinary recovery fallback."),
                [new(RecoveryDisposition.SafeToResumeFromPhase,
                    (operation, _, _) => { if (operation.OperationId == denied.OperationId && ++checks == 2) throw new UnauthorizedAccessException(); },
                    (execution, _) => { visited.Add(execution.Current.OperationId); execution.Transition("Done", Admit); return Task.CompletedTask; })])], f.Time);
        runtime.ApplyStartupRecovery(); await Wait(runtime.WaitForCurrentWorkersAsync()); var state = runtime.Read();
        Check(checks == 2 && visited.Count == 1 && visited.Single() == allowed.OperationId);
        Check(state.Operations.Single(o => o.Operation.OperationId == denied.OperationId).Operation == denied);
        Check(state.Operations.Single(o => o.Operation.OperationId == denied.OperationId).Status == HostOperationStatus.RecoveryRequired);
        Check(state.DurableState.Locks.Single() == deniedLock && state.DurableState.Revision == before.Revision + 3);
        Check(state.Operations.Single(o => o.Operation.OperationId == allowed.OperationId).Status == HostOperationStatus.Resolved);
    }
}
