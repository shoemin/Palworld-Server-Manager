using Microsoft.AspNetCore.Connections;

namespace PalworldServerManager.Host;

// One-way lifetime for a Host credential generation, under the existing machine lease.
// This is not an operation lock, authorization decision, or permission to change Current.
internal sealed class HostTrafficLifetime : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int active, disposed;
    private bool closed;

    private Admission Enter(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (closed) throw new InvalidOperationException("Host traffic is closed.");
            active = checked(active + 1);
            return new(this, stopping.Token);
        }
    }
    private void Leave()
    {
        lock (gate)
        {
            active--;
            if (closed && active == 0) idle.TrySetResult();
        }
    }
    private sealed class Admission(HostTrafficLifetime owner, CancellationToken token) : IDisposable
    {
        private int released;
        internal CancellationToken Stopping => token;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) owner.Leave(); }
    }

    // Cover the entire operation, including post-reply persistence, audit and transport disposal.
    internal async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var admission = Enter(ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, admission.Stopping);
        linked.Token.ThrowIfCancellationRequested();
        return await work(linked.Token).ConfigureAwait(false);
    }
    internal Task RunAsync(Func<CancellationToken, Task> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(async token => { await work(token).ConfigureAwait(false); return true; }, ct);
    }

    // Install BEFORE TLS, not just around the RPC handler: incomplete handshakes own the key.
    internal ConnectionDelegate BindConnection(ConnectionDelegate next) => async connection =>
    {
        using var admission = Enter();
        using var abort = admission.Stopping.Register(() => connection.Abort(new ConnectionAbortedException("Host traffic is stopping.")));
        admission.Stopping.ThrowIfCancellationRequested();
        await next(connection).ConfigureAwait(false);
    };

    internal Task DrainAsync()
    {
        bool start;
        lock (gate)
        {
            start = !closed; closed = true;
            if (active == 0) idle.TrySetResult();
        }
        if (start) _ = DrainCoreAsync();
        return drained.Task;
    }
    private async Task DrainCoreAsync()
    {
        Exception? failure = null;
        // Cancel outside our lock; callbacks may finish admissions or reenter DrainAsync.
        try { await stopping.CancelAsync().ConfigureAwait(false); }
        catch (Exception ex) { failure = ex; }
        await idle.Task.ConfigureAwait(false);
        if (failure is null) drained.TrySetResult(); else drained.TrySetException(failure);
    }
    public async ValueTask DisposeAsync()
    {
        try { await DrainAsync().ConfigureAwait(false); }
        finally { if (Interlocked.Exchange(ref disposed, 1) == 0) stopping.Dispose(); }
    }
}
