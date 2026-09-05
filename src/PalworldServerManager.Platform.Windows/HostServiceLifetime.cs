namespace PalworldServerManager.Platform.Windows;

/// <summary>
/// The startup-readiness and deterministic-shutdown state machine for the Host service, factored
/// out of <see cref="System.ServiceProcess.ServiceBase"/> so it is unit-testable without a real
/// SCM-registered service.
///
/// STARTUP READINESS: <see cref="Start"/> blocks until the runtime signals readiness (via the
/// <see cref="TaskCompletionSource{TResult}"/> it is handed) or fails trying - never merely
/// checking whether the launching Task is immediately faulted. Without this, SCM could report the
/// service Running while the #40 exclusivity lock/database were still mid-acquisition, or even
/// after acquisition had already failed, leaving a hollow "Running" process with no authoritative
/// state (HOST-001, PERSIST-001).
///
/// DETERMINISTIC SHUTDOWN: <see cref="StopAndWait"/> blocks for the runtime's actual completion
/// rather than a bounded timeout - a timeout here would let shutdown report success while
/// resources (the exclusivity lock, the open database) might still be held, which is worse than
/// blocking: the next start would then race a lock that is not really free yet.
/// </summary>
public sealed class HostServiceLifetime : IDisposable
{
    private readonly Func<CancellationToken, TaskCompletionSource<bool>, Task> _runAsync;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _running;

    public HostServiceLifetime(Func<CancellationToken, TaskCompletionSource<bool>, Task> runAsync)
    {
        _runAsync = runAsync ?? throw new ArgumentNullException(nameof(runAsync));
    }

    /// <summary>
    /// Starts the runtime and blocks until it signals readiness or fails trying. Throws the
    /// runtime's own failure (never a generic wrapper) so the caller - ordinarily SCM via
    /// ServiceBase.OnStart - sees the precise cause of a failed start.
    /// </summary>
    public void Start()
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _running = Task.Run(async () =>
        {
            try
            {
                await _runAsync(_stopping.Token, ready).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // Ordinary stop.
            }
        });

        var completed = Task.WhenAny(ready.Task, _running).GetAwaiter().GetResult();

        if (completed == _running)
        {
            // The runtime task finished - normally or abnormally - before ever signaling ready.
            if (_running.IsFaulted)
            {
                throw _running.Exception!.GetBaseException();
            }

            throw new InvalidOperationException("The Host runtime stopped before completing startup.");
        }

        // Surfaces the runtime's own exception if it used TrySetException instead of TrySetResult.
        ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Signals cancellation and blocks until the runtime has actually finished releasing its
    /// resources. Ordinary cancellation is not an error; an unexpected runtime fault propagates
    /// rather than being silently suppressed.
    /// </summary>
    public void StopAndWait()
    {
        _stopping.Cancel();
        _running?.GetAwaiter().GetResult();
    }

    public void Dispose() => _stopping.Dispose();
}
