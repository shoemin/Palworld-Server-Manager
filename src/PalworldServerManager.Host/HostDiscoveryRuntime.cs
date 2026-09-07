using System.Net;
using System.Runtime.ExceptionServices;
using PalworldServerManager.Contracts;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

// Trusted composition supplies operational protocol/bound ports and a LAN-filtering receiver.
// No listener, secret, durable state or authority is created by an advertisement.
internal sealed class HostDiscoveryRuntime : IAsyncDisposable
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    private readonly Func<LanDiscoveryReceived, ILanDiscoveryReceiver> createReceiver;
    private readonly ILanDiscoveryBroadcaster broadcaster;
    private readonly HostDiscoveryDirectory directory;
    private readonly byte[] packet;
    private readonly TimeProvider time;
    private readonly CancellationTokenSource stopping;
    private readonly TaskCompletionSource stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private bool closed;
    internal Task Completion => completed.Task;

    private HostDiscoveryRuntime(UnverifiedHostAdvertisement advertisement,
        Func<LanDiscoveryReceived, ILanDiscoveryReceiver> createReceiver, ILanDiscoveryBroadcaster broadcaster, TimeProvider time)
    {
        this.packet = HostDiscoveryCodec.Encode(advertisement);
        this.createReceiver = createReceiver; this.broadcaster = broadcaster; this.time = time;
        directory = new(advertisement.ClaimedHostId, time);
        stopping = new();
    }

    internal static async Task<HostDiscoveryRuntime> StartAsync(UnverifiedHostAdvertisement advertisement,
        Func<LanDiscoveryReceived, ILanDiscoveryReceiver> createReceiver, ILanDiscoveryBroadcaster broadcaster,
        CancellationToken cancellationToken = default, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(createReceiver); ArgumentNullException.ThrowIfNull(broadcaster);
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = new HostDiscoveryRuntime(advertisement, createReceiver, broadcaster, time ?? TimeProvider.System);
        ILanDiscoveryReceiver? initial = null;
        try
        {
            try { initial = runtime.CreateReceiver(); }
            catch (LanDiscoveryUnavailableException) { /* Clean temporary startup outage: delay before a fresh attempt. */ }
            cancellationToken.ThrowIfCancellationRequested();
            _ = Task.Run(() => runtime.SuperviseAsync(initial));
            return runtime;
        }
        catch (Exception primary)
        {
            await runtime.RequestStop().ConfigureAwait(false);
            Exception failure = primary;
            if (initial is not null)
            {
                try { await initial.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { failure = new AggregateException(primary, cleanup); }
            }
            runtime.stopping.Dispose();
            ExceptionDispatchInfo.Capture(failure).Throw(); throw;
        }
    }

    private ILanDiscoveryReceiver CreateReceiver() => createReceiver(ReceiveAsync)
        ?? throw new InvalidOperationException("Discovery receiver factory returned no owner.");

    private ValueTask ReceiveAsync(int actualInterfaceIndex, IPAddress actualSource, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        lock (gate)
        {
            if (!closed && !token.IsCancellationRequested) directory.Observe(actualSource, bytes.Span);
        }
        return ValueTask.CompletedTask;
    }

    internal IReadOnlyList<UnverifiedHostEndpoint> Snapshot()
    {
        lock (gate) { ObjectDisposedException.ThrowIf(closed, this); return directory.Snapshot(); }
    }

    private async Task ReceiveLoopAsync(ILanDiscoveryReceiver? receiver)
    {
        var token = stopping.Token;
        if (receiver is null) await Task.Delay(Interval, time, token).ConfigureAwait(false);
        while (true)
        {
            // Even if stop raced initial construction, the returned owner must be disposed.
            if (receiver is null)
            {
                token.ThrowIfCancellationRequested();
                try { receiver = CreateReceiver(); }
                catch (LanDiscoveryUnavailableException)
                { await Task.Delay(Interval, time, token).ConfigureAwait(false); continue; }
            }
            try { await WaitAndDisposeAsync(receiver, token).ConfigureAwait(false); }
            catch (LanDiscoveryUnavailableException) { /* Only a clean, top-level native outage permits retry. */ }
            receiver = null;
            await Task.Delay(Interval, time, token).ConfigureAwait(false);
        }
    }

    private async Task WaitAndDisposeAsync(ILanDiscoveryReceiver receiver, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            // A blocked cancellation callback must not delay initiating receiver disposal.
            await Task.WhenAny(receiver.Completion, stopRequested.Task).ConfigureAwait(false);
            if (receiver.Completion.IsCompleted) await receiver.Completion.ConfigureAwait(false);
            if (!stopRequested.Task.IsCompleted) throw new InvalidOperationException("Discovery receiver ended unexpectedly.");
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == token && token.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        // Close admission and stop the other loop as soon as a fatal receiver failure is known,
        // including while this receiver's disposal remains blocked.
        if (failure is not null and not LanDiscoveryUnavailableException) _ = RequestStop();
        try { await receiver.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanup)
        {
            // Concrete receivers rethrow their already-drained Completion failure from Dispose.
            // A different disposal failure is never permission to retry, even if it uses the marker.
            if (!ReferenceEquals(failure, cleanup))
            {
                if (failure is null && receiver.Completion.Exception is { InnerExceptions.Count: 1 } completedFailure
                    && ReferenceEquals(completedFailure.InnerExceptions[0], cleanup)) failure = cleanup;
                else failure = failure is null ? new AggregateException(cleanup) : new AggregateException(failure, cleanup);
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task SendLoopAsync()
    {
        var token = stopping.Token;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { await broadcaster.BroadcastAsync(packet, token).ConfigureAwait(false); }
            catch (LanDiscoveryUnavailableException) { }
            await Task.Delay(Interval, time, token).ConfigureAwait(false);
        }
    }

    private async Task<Exception?> ObserveWorkerAsync(Func<Task> work)
    {
        try { await work().ConfigureAwait(false); return new InvalidOperationException("Discovery worker ended unexpectedly."); }
        catch (OperationCanceledException ex) when (ex.CancellationToken == stopping.Token && stopping.IsCancellationRequested) { return null; }
        catch (Exception ex) { return ex; }
        finally { _ = RequestStop(); }
    }

    private async Task SuperviseAsync(ILanDiscoveryReceiver? initial)
    {
        var receive = Task.Run(() => ObserveWorkerAsync(() => ReceiveLoopAsync(initial)));
        var send = Task.Run(() => ObserveWorkerAsync(SendLoopAsync));
        var failures = (await Task.WhenAll(receive, send).ConfigureAwait(false)).OfType<Exception>().ToList();
        try { await RequestStop().ConfigureAwait(false); }
        catch (Exception ex) { failures.Add(ex); }
        try { stopping.Dispose(); } catch (Exception ex) { failures.Add(ex); }
        var distinct = failures.Distinct(ReferenceEqualityComparer.Instance).Cast<Exception>().ToArray();
        if (distinct.Length == 0) completed.TrySetResult();
        else completed.TrySetException(distinct.Length == 1 ? distinct[0] : new AggregateException(distinct));
    }

    private Task RequestStop()
    {
        bool start;
        lock (gate) { start = !closed; closed = true; }
        if (start) { stopRequested.TrySetResult(); _ = CancelAsync(); }
        return cancelled.Task;
    }
    private async Task CancelAsync()
    {
        try { await stopping.CancelAsync().ConfigureAwait(false); cancelled.TrySetResult(); }
        catch (Exception ex) { cancelled.TrySetException(ex); }
    }
    public ValueTask DisposeAsync() { _ = RequestStop(); return new(Completion); }
}
