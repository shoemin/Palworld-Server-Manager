using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Operations;

namespace PalworldServerManager.Host;

// The concrete kind's author explicitly supplies each supported recovery action and its
// current authority check. Missing handlers are never replaced by the ordinary executor.
internal sealed class HostOperationRecoveryHandler
{
    public RecoveryDisposition Disposition { get; }
    public Action<DurableOperation, SqliteConnection, SqliteTransaction> RequireCurrentAuthority { get; }
    public Func<HostOperationExecution, CancellationToken, Task> ExecuteAsync { get; }

    public HostOperationRecoveryHandler(RecoveryDisposition disposition,
        Action<DurableOperation, SqliteConnection, SqliteTransaction> requireCurrentAuthority,
        Func<HostOperationExecution, CancellationToken, Task> executeAsync)
    {
        if (disposition is not (RecoveryDisposition.SafeToRetryFromStart or RecoveryDisposition.SafeToResumeFromPhase or RecoveryDisposition.SafeToDiscard))
            throw new ArgumentException("Manual or unknown recovery cannot execute automatically.");
        Disposition = disposition;
        RequireCurrentAuthority = requireCurrentAuthority ?? throw new ArgumentNullException(nameof(requireCurrentAuthority));
        ExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
    }
}
