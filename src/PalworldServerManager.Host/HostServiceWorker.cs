namespace PalworldServerManager.Host;

// OnStart must return independently of CNG/Schannel startup. All asynchronous resources,
// including the Host lease, belong to run and finish before shutdown returns.
public sealed class HostServiceWorker : IDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly Task _worker;
    private int _disposed;
    public HostServiceWorker(Func<CancellationToken, Task> run, Action failed, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(run); ArgumentNullException.ThrowIfNull(failed);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _worker = Task.Run(async () =>
        {
            try
            {
                await run(_stop.Token).ConfigureAwait(false);
                if (!_stop.IsCancellationRequested) failed(); // unexpected listener exit is fatal
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            // A faulted background Task does not terminate the service by itself. Even a
            // resource-exhaustion failure must reach the minimal fatal exit callback.
            catch (Exception) { failed(); }
        });
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _stop.Cancel(); _worker.GetAwaiter().GetResult(); }
        finally { _stop.Dispose(); }
    }
}
