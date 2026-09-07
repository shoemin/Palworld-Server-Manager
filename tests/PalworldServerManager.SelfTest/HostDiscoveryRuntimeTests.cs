using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PalworldServerManager.Contracts;
using PalworldServerManager.Host;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static class HostDiscoveryRuntimeTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Host discovery runtime assertion failed."); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Bounded(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task Until(Func<bool> predicate)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!predicate()) await Task.Delay(1, deadline.Token); }
    private static async Task<Exception> Failure(Task task)
    { try { await Bounded(task); } catch (Exception ex) when (ex is not TimeoutException) { return ex; } throw new Exception("Expected discovery failure."); }
    private static UnverifiedHostAdvertisement Advertisement() => new(Guid.NewGuid(), 9, 23, 5000, 5001);
    private static LanDiscoveryUnavailableException Temporary() => new(new SocketException((int)SocketError.NetworkDown));

    private sealed class Clock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<Timer> timers = [];
        private long stamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() { lock (gate) return stamp; }
        internal int Pending { get { lock (gate) return timers.Count; } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Check(period == Timeout.InfiniteTimeSpan);
            var timer = new Timer(this, callback, state); timer.Change(dueTime, period); return timer;
        }
        internal void Advance(int milliseconds)
        {
            Timer[] due;
            lock (gate) { stamp += milliseconds; due = timers.Where(t => t.Due <= stamp).ToArray(); foreach (var t in due) timers.Remove(t); }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback;
            internal readonly object? State = state;
            internal long Due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock.gate)
                { clock.timers.Remove(this); if (dueTime != Timeout.InfiniteTimeSpan) { Due = clock.stamp + (long)dueTime.TotalMilliseconds; clock.timers.Add(this); } }
                return true;
            }
            public void Dispose() { lock (clock.gate) clock.timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Receiver(LanDiscoveryReceived callback) : ILanDiscoveryReceiver
    {
        internal readonly TaskCompletionSource End = Signal(), Disposing = Signal();
        internal Task Release = Task.CompletedTask;
        internal Exception? CleanupFailure;
        internal int Disposes;
        public int Port => 45678;
        public Task Completion => End.Task;
        internal ValueTask Emit(IPAddress source, byte[] bytes) => callback(7, source, bytes, CancellationToken.None);
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposes); Disposing.TrySetResult(); await Release;
            if (CleanupFailure is not null) throw CleanupFailure;
            End.TrySetResult(); await End.Task;
        }
    }
    private sealed class Broadcaster : ILanDiscoveryBroadcaster
    {
        internal int Calls, Active;
        internal Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<int>> Send = (_, _) => ValueTask.FromResult(0);
        public async ValueTask<int> BroadcastAsync(ReadOnlyMemory<byte> packet, CancellationToken token)
        {
            Check(Interlocked.Increment(ref Active) == 1); Interlocked.Increment(ref Calls);
            try { return await Send(packet, token); } finally { Interlocked.Decrement(ref Active); }
        }
    }

    public static async Task StartupMetadataAndExpiry()
    {
        var ad = Advertisement(); var clock = new Clock(); var sends = new Broadcaster(); var opens = 0;
        Check(await Failure(HostDiscoveryRuntime.StartAsync(ad with { PeerPort = 0 }, _ => { opens++; return null!; }, sends)) is ArgumentException);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Check(await Failure(HostDiscoveryRuntime.StartAsync(ad, _ => { opens++; return null!; }, sends, cancelled.Token)) is OperationCanceledException);
        Check(opens == 0);
        var denied = new SocketException((int)SocketError.AccessDenied);
        Check(ReferenceEquals(await Failure(HostDiscoveryRuntime.StartAsync(ad, _ => throw denied, sends)), denied));
        Receiver? acquired = null;
        using var stopDuringFactory = new CancellationTokenSource();
        Check(await Failure(HostDiscoveryRuntime.StartAsync(ad, callback =>
        { acquired = new(callback); stopDuringFactory.Cancel(); return acquired; }, sends, stopDuringFactory.Token)) is OperationCanceledException);
        Check(acquired!.Disposes == 1 && acquired.Completion.IsCompletedSuccessfully && sends.Calls == 0);
        using var badStop = new CancellationTokenSource(); var cleanup = new IOException("startup cleanup");
        var startupFailure = await Failure(HostDiscoveryRuntime.StartAsync(ad, callback =>
        { acquired = new(callback) { CleanupFailure = cleanup }; badStop.Cancel(); return acquired; }, sends, badStop.Token));
        Check(startupFailure is AggregateException errors && errors.InnerExceptions[0] is OperationCanceledException && ReferenceEquals(errors.InnerExceptions[1], cleanup));

        Receiver? receiver = null;
        sends.Send = (bytes, _) => { Check(HostDiscoveryCodec.Decode(bytes.Span) == ad); return ValueTask.FromResult(1); };
        await using var runtime = await HostDiscoveryRuntime.StartAsync(ad, callback => receiver = new(callback), sends, time: clock);
        await Until(() => clock.Pending == 1);
        Check(sends.Calls == 1 && runtime.Snapshot().Count == 0);
        var remote = ad with { ClaimedHostId = Guid.NewGuid() };
        await receiver!.Emit(IPAddress.Parse("192.0.2.7"), HostDiscoveryCodec.Encode(remote));
        await receiver.Emit(IPAddress.Parse("192.0.2.8"), HostDiscoveryCodec.Encode(remote));
        await receiver.Emit(IPAddress.Parse("192.0.2.9"), HostDiscoveryCodec.Encode(ad));
        var snapshot = runtime.Snapshot(); Check(snapshot.Count == 2 && snapshot[0].PeerAddress.Host == "192.0.2.7");
        clock.Advance(12000); Check(runtime.Snapshot().Count == 0 && snapshot.Count == 2);
        await Bounded(runtime.DisposeAsync().AsTask()); Check(receiver.Disposes == 1 && clock.Pending == 0);
        Check(await Failure(Task.Run(() => runtime.Snapshot())) is ObjectDisposedException);
        await receiver.Emit(IPAddress.Parse("192.0.2.7"), HostDiscoveryCodec.Encode(remote));
    }

    public static async Task BoundedRetriesAndFreshReceiver()
    {
        var clock = new Clock(); var sends = new Broadcaster(); var opens = 0; Receiver? receiver = null;
        sends.Send = (_, _) => sends.Calls == 1 ? ValueTask.FromException<int>(Temporary()) : ValueTask.FromResult(0);
        await using var runtime = await HostDiscoveryRuntime.StartAsync(Advertisement(), callback =>
        { if (Interlocked.Increment(ref opens) == 1) throw Temporary(); return receiver = new(callback); }, sends, time: clock);
        await Until(() => clock.Pending == 2); Check(opens == 1 && sends.Calls == 1);
        clock.Advance(2999); Check(clock.Pending == 2 && opens == 1 && sends.Calls == 1);
        clock.Advance(1); await Until(() => receiver is not null && sends.Calls == 2 && clock.Pending == 1);
        var old = receiver!; var release = Signal(); old.Release = release.Task;
        old.End.SetException(Temporary()); await Bounded(old.Disposing.Task);
        clock.Advance(30000); await Until(() => sends.Calls == 3 && clock.Pending == 1);
        Check(opens == 2 && !runtime.Completion.IsCompleted); // No recreation before full disposal, even across many intervals.
        release.SetResult(); await Until(() => clock.Pending == 2);
        clock.Advance(2999); Check(opens == 2 && clock.Pending == 2);
        clock.Advance(1); await Until(() => opens == 3 && clock.Pending == 1);
        Check(!ReferenceEquals(old, receiver) && old.Disposes == 1 && sends.Calls == 4);
        await Bounded(runtime.DisposeAsync().AsTask()); Check(receiver!.Disposes == 1 && clock.Pending == 0);
    }

    public static async Task StopDrainsEveryOwnedActivity()
    {
        var clock = new Clock(); var sendEntered = Signal(); var releaseSend = Signal();
        var cancelEntered = Signal(); var releaseCancel = Signal(); var releaseReceive = Signal(); Receiver? receiver = null;
        var sends = new Broadcaster { Send = async (_, token) =>
        {
            using var registration = token.Register(() => { cancelEntered.SetResult(); releaseCancel.Task.GetAwaiter().GetResult(); });
            sendEntered.SetResult(); await releaseSend.Task; return 1;
        } };
        var runtime = await HostDiscoveryRuntime.StartAsync(Advertisement(), callback => receiver = new(callback) { Release = releaseReceive.Task }, sends, time: clock);
        try
        {
            await Bounded(sendEntered.Task); clock.Advance(300000); Check(sends.Calls == 1 && clock.Pending == 0);
            var first = runtime.DisposeAsync().AsTask(); var second = runtime.DisposeAsync().AsTask(); Check(ReferenceEquals(first, second));
            await Bounded(cancelEntered.Task); await Bounded(receiver!.Disposing.Task); Check(!first.IsCompleted && sends.Active == 1);
            releaseReceive.SetResult(); await Until(() => receiver.Completion.IsCompleted); Check(!first.IsCompleted);
            releaseSend.SetResult(); Check(!first.IsCompleted); // Send registration disposal also waits for its running cancellation callback.
            releaseCancel.SetResult(); await Bounded(first);
            Check(sends.Active == 0 && sends.Calls == 1 && receiver.Disposes == 1 && clock.Pending == 0);
        }
        finally { releaseReceive.TrySetResult(); releaseSend.TrySetResult(); releaseCancel.TrySetResult(); await runtime.DisposeAsync(); }
    }

    public static async Task FatalFailuresNeverRetryOrHideCleanup()
    {
        var releaseFatal = Signal(); Receiver? draining = null;
        var knownFatal = new IOException("fatal before cleanup");
        var earlyStop = await HostDiscoveryRuntime.StartAsync(Advertisement(), callback => draining = new(callback) { Release = releaseFatal.Task }, new Broadcaster());
        try
        {
            draining!.End.SetException(knownFatal); await Bounded(draining.Disposing.Task);
            Check(!earlyStop.Completion.IsCompleted);
            Check(await Failure(Task.Run(() => earlyStop.Snapshot())) is ObjectDisposedException);
            releaseFatal.SetResult(); Check(ReferenceEquals(await Failure(earlyStop.Completion), knownFatal));
        }
        finally { releaseFatal.TrySetResult(); await Failure(earlyStop.DisposeAsync().AsTask()); }
        foreach (var cause in new Exception[] { new SocketException((int)SocketError.NetworkDown), new IOException("callback"),
            new OperationCanceledException(new CancellationToken(true)), new AggregateException(Temporary()) })
        {
            var clock = new Clock(); Receiver? receiver = null; var opens = 0;
            var runtime = await HostDiscoveryRuntime.StartAsync(Advertisement(), callback => { opens++; return receiver = new(callback); }, new Broadcaster(), time: clock);
            receiver!.End.SetException(cause);
            Check(ReferenceEquals(await Failure(runtime.Completion), cause) && runtime.Completion.IsFaulted);
            Check(ReferenceEquals(await Failure(runtime.DisposeAsync().AsTask()), cause));
            clock.Advance(300000); Check(opens == 1 && receiver.Disposes == 1 && clock.Pending == 0);
        }
        foreach (var primary in new Exception?[] { null, Temporary() })
        {
            Receiver? receiver = null; var marker = Temporary();
            var runtime = await HostDiscoveryRuntime.StartAsync(Advertisement(), callback => receiver = new(callback) { CleanupFailure = marker }, new Broadcaster());
            if (primary is null) receiver!.End.SetResult(); else receiver!.End.SetException(primary);
            var failure = await Failure(runtime.Completion);
            Check(failure is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Contains(marker));
            if (primary is not null) Check(((AggregateException)failure).InnerExceptions.Contains(primary));
            Check(ReferenceEquals(await Failure(runtime.DisposeAsync().AsTask()), failure) && receiver.Disposes == 1);
        }
        Receiver? ended = null;
        var successfulEnd = await HostDiscoveryRuntime.StartAsync(Advertisement(), callback => ended = new(callback), new Broadcaster());
        ended!.End.SetResult(); Check(await Failure(successfulEnd.Completion) is InvalidOperationException);
        await Failure(successfulEnd.DisposeAsync().AsTask());
    }

    public static async Task SenderFailureDrainsReceiverAndCancellation()
    {
        var entered = Signal(); var fail = Signal(); var release = Signal(); Receiver? receiver = null;
        var sendFailure = new IOException("send failure"); var cleanupFailure = new IOException("receiver cleanup"); var cancelFailure = new IOException("cancellation cleanup");
        CancellationTokenRegistration registration = default;
        var sends = new Broadcaster { Send = async (_, token) =>
        { registration = token.Register(() => throw cancelFailure); entered.SetResult(); await fail.Task; throw sendFailure; } };
        var runtime = await HostDiscoveryRuntime.StartAsync(Advertisement(), callback => receiver = new(callback) { Release = release.Task, CleanupFailure = cleanupFailure }, sends);
        try
        {
            await Bounded(entered.Task); fail.SetResult(); await Bounded(receiver!.Disposing.Task); Check(!runtime.Completion.IsCompleted);
            release.SetResult(); var failure = await Failure(runtime.Completion);
            Check(failure is AggregateException errors && new[] { sendFailure, cleanupFailure, cancelFailure }.All(errors.Flatten().InnerExceptions.Contains));
            Check(ReferenceEquals(await Failure(runtime.DisposeAsync().AsTask()), failure));
        }
        finally { fail.TrySetResult(); release.TrySetResult(); await Failure(runtime.DisposeAsync().AsTask()); registration.Dispose(); }
    }

    public static async Task ActualLoopbackPacketsAndRelease()
    {
        var index = NetworkInterface.GetAllNetworkInterfaces().Single(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback).GetIPProperties().GetIPv4Properties()!.Index;
        WindowsLanDiscoveryReceiver? receiver = null; var observed = Signal(); var sent = Signal(); var clock = new Clock(); var ad = Advertisement();
        var sends = new Broadcaster { Send = async (bytes, token) =>
        {
            using var sender = new WindowsLanDiscoveryBroadcaster.DatagramSender(IPAddress.Loopback, index);
            var result = await sender.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, receiver!.Port), token);
            Check(result == bytes.Length); sent.TrySetResult(); return 1;
        } };
        var runtime = await HostDiscoveryRuntime.StartAsync(ad, callback => receiver = new(async (actualIndex, source, bytes, token) =>
        { await callback(actualIndex, source, bytes, token); if (HostDiscoveryCodec.Decode(bytes.Span)?.ClaimedHostId != ad.ClaimedHostId) observed.TrySetResult(); },
            (actualIndex, source) => actualIndex == index && IPAddress.IsLoopback(source)), sends, time: clock);
        var port = receiver!.Port;
        try
        {
            await Bounded(sent.Task);
            using var sender = new WindowsLanDiscoveryBroadcaster.DatagramSender(IPAddress.Loopback, index);
            await sender.SendAsync(HostDiscoveryCodec.Encode(ad with { ClaimedHostId = Guid.NewGuid() }), new(IPAddress.Loopback, port), CancellationToken.None);
            await Bounded(observed.Task); var found = runtime.Snapshot();
            Check(found.Count == 1 && found[0].PeerAddress.Host == "127.0.0.1" && found[0].PairingAddress.Port == ad.PairingPort);
        }
        finally { await Bounded(runtime.DisposeAsync().AsTask()); }
        using var rebind = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        rebind.Bind(new IPEndPoint(IPAddress.Loopback, port)); Check(receiver.Completion.IsCompletedSuccessfully && clock.Pending == 0);
    }
}
