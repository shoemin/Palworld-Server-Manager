using PalworldServerManager.Core.Operations;

namespace PalworldServerManager.Host;

internal sealed partial class HostOperationRuntime
{
    // The Host composition calls this once before exposing normal admission. Classification
    // is synchronous; eligible recovery work is Host-owned and drained like ordinary work.
    public void ApplyStartupRecovery()
    {
        lock (gate)
        {
            if (stopping) throw new ObjectDisposedException(nameof(HostOperationRuntime));
            if (recoveryInitialized) return;
            var state = repository.Read(stop.Token);
            recoveryInitialized = true;
            foreach (var observation in state.Operations.Where(o => !o.Operation.IsTerminal))
            {
                var op = observation.Operation;
                if (state.HasUnqualifiedState || !observation.PolicyIsCurrent || op.Recovery is null ||
                    op.Recovery == RecoveryDisposition.RequiresManualReview ||
                    !executors.TryGetValue(op.Kind, out var executor) || !executor.RecoveryHandlers.TryGetValue(op.Recovery.Value, out var handler))
                { returnedWorkers.Add(op.OperationId); continue; }
                HostOperationExecution? execution = null;
                try
                {
                    var prepared = repository.PrepareRecovery(op.OperationId, op.Revision, op.Recovery.Value,
                        (c, tx) => handler.RequireCurrentAuthority(op, c, tx), stop.Token);
                    execution = new(repository, prepared, stop.Token);
                    var owned = execution;
                    workers.Add(op.OperationId, Queue(() => RunAsync(handler.ExecuteAsync, owned)));
                }
                catch (Exception)
                {
                    execution?.Seal(); returnedWorkers.Add(op.OperationId);
                    // Preparation rolled back, or the prepared locked record is retained if
                    // dispatch failed. Never retry in a loop or expose raw failure details.
                }
            }
        }
    }
}
