using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Operations;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

internal enum HostOperationStatus { Running, Resolved, AwaitingRecovery, RecoveryRequired }
internal sealed record HostOperationObservation(DurableOperation Operation, HostOperationStatus Status);
internal sealed record HostOperationRuntimeSnapshot(OperationStateSnapshot DurableState,
    IReadOnlyList<HostOperationObservation> Operations);

// One instance belongs to the authoritative Host lifetime, outside listener generations.
// The owner holds the machine lease until DisposeAsync has drained every actual worker.
// This is trusted code registration, not an RPC, command queue or executor sandbox.
internal sealed class HostOperationRuntime : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly OperationRepository repository;
    private readonly Dictionary<string, HostOperationExecutor> executors = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Task> workers = [];
    private readonly HashSet<Guid> returnedWorkers = [];
    private readonly CancellationTokenSource stop;
    private Task? shutdown;
    private bool stopping;

    internal HostOperationRuntime(HostDatabase database, Guid hostId, IEnumerable<HostOperationExecutor> registrations,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        foreach (var executor in registrations)
            if (executor?.Definition is null || executor.ExecuteAsync is null || !executors.TryAdd(executor.Definition.Kind, executor))
                throw new ArgumentException("Unique trusted operation executors required.");
        repository = new(database, hostId, executors.Values.Select(e => e.Definition), timeProvider);
        // Inspect every persisted record now; none is claimed as a living worker. Explicit
        // per-phase startup execution is a separate framework unit, not an implicit retry.
        repository.Read();
        stop = new();
    }

    public DurableOperation Start(Guid id, string kind, OperationTarget target,
        Action<SqliteConnection, SqliteTransaction> requireCurrentAuthority, CancellationToken admissionCancellation = default)
    {
        lock (gate)
        {
            if (stopping) throw new ObjectDisposedException(nameof(HostOperationRuntime));
            PruneCompletedTasks();
            ArgumentNullException.ThrowIfNull(kind);
            if (!executors.TryGetValue(kind, out var executor)) throw new ArgumentException("Unknown trusted operation kind.");
            var record = repository.Start(id, kind, target, requireCurrentAuthority, admissionCancellation);
            var execution = new HostOperationExecution(repository, record, stop.Token);
            try { workers.Add(id, Queue(() => RunAsync(executor, execution))); }
            catch { execution.Seal(); returnedWorkers.Add(id); throw; }
            return record;
        }
    }

    public HostOperationRuntimeSnapshot Read(CancellationToken observationCancellation = default)
    {
        lock (gate)
        {
            var state = repository.Read(observationCancellation);
            PruneCompletedTasks();
            returnedWorkers.ExceptWith(state.Operations.Where(o => o.Operation.IsTerminal).Select(o => o.Operation.OperationId));
            var observations = state.Operations.Select(o => new HostOperationObservation(o.Operation,
                o.Operation.IsTerminal ? HostOperationStatus.Resolved :
                state.HasUnqualifiedState || !o.PolicyIsCurrent ? HostOperationStatus.RecoveryRequired :
                returnedWorkers.Contains(o.Operation.OperationId) ? HostOperationStatus.RecoveryRequired :
                workers.ContainsKey(o.Operation.OperationId) ? HostOperationStatus.Running :
                o.Operation.Recovery == RecoveryDisposition.RequiresManualReview
                    ? HostOperationStatus.RecoveryRequired : HostOperationStatus.AwaitingRecovery)).ToArray();
            return new(state, Array.AsReadOnly(observations));
        }
    }

    // Observational wait for the current task set only. Cancellation never touches workers.
    internal Task WaitForCurrentWorkersAsync(CancellationToken waitCancellation = default)
    {
        Task[] tasks; lock (gate) tasks = workers.Values.ToArray();
        return Task.WhenAll(tasks).WaitAsync(waitCancellation);
    }

    private async Task RunAsync(HostOperationExecutor executor, HostOperationExecution execution)
    {
        try { await executor.ExecuteAsync(execution, stop.Token).ConfigureAwait(false); }
        catch (Exception) { /* Preserve durable state; no inferred terminal phase or raw exception log. */ }
        finally
        {
            execution.Seal();
            lock (gate) if (!execution.Current.IsTerminal) returnedWorkers.Add(execution.Current.OperationId);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (shutdown is not null) return new(shutdown);
            stopping = true;
            var tasks = workers.Values.ToArray();
            shutdown = Queue(async () =>
            {
                Exception? cancellationFailure = null;
                try { stop.Cancel(); } catch (Exception error) { cancellationFailure = error; }
                try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                finally { stop.Dispose(); }
                if (cancellationFailure is not null) throw new InvalidOperationException("Host worker cancellation callback failed after drain.");
            });
            return new(shutdown);
        }
    }

    private static Task Queue(Func<Task> action)
    {
        if (ExecutionContext.IsFlowSuppressed()) return Task.Run(action);
        using (ExecutionContext.SuppressFlow()) return Task.Run(action);
    }

    // A finally block still belongs to a live Task. Only prune tasks proven completed,
    // so shutdown and observer waits cannot miss a task while it is exiting.
    private void PruneCompletedTasks()
    {
        foreach (var id in workers.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToArray()) workers.Remove(id);
    }
}
