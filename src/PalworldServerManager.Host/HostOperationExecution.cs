using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Operations;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

// Trusted executor code must await all work it owns, publish terminal state only after
// actual resolution, and enforce current business authority/audit at each effect boundary.
internal sealed record HostOperationExecutor(OperationDefinition Definition,
    Func<HostOperationExecution, CancellationToken, Task> ExecuteAsync);

internal sealed class HostOperationExecution
{
    private readonly object gate = new();
    private readonly OperationRepository repository;
    private readonly CancellationToken hostStop;
    private DurableOperation current;
    private bool closed;

    internal HostOperationExecution(OperationRepository repository, DurableOperation operation, CancellationToken hostStop)
    { this.repository = repository; current = operation; this.hostStop = hostStop; }

    public DurableOperation Current { get { lock (gate) return current; } }

    public DurableOperation Transition(string nextPhase, Action<SqliteConnection, SqliteTransaction> requireCurrentAuthority)
    {
        lock (gate)
        {
            RequireOpen();
            return current = repository.Transition(current.OperationId, current.Revision, nextPhase, requireCurrentAuthority, hostStop);
        }
    }

    public DurableOperation Heartbeat(Action<SqliteConnection, SqliteTransaction> requireCurrentAuthority)
    {
        lock (gate)
        {
            RequireOpen();
            return current = repository.Heartbeat(current.OperationId, current.Revision, requireCurrentAuthority, hostStop);
        }
    }

    internal void Seal() { lock (gate) closed = true; }
    private void RequireOpen()
    {
        if (closed) throw new ObjectDisposedException(nameof(HostOperationExecution));
        hostStop.ThrowIfCancellationRequested();
    }
}
