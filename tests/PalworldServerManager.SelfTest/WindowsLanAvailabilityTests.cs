using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
using Availability = PalworldServerManager.Platform.Windows.WindowsLanAvailability;
using Broadcaster = PalworldServerManager.Platform.Windows.WindowsLanDiscoveryBroadcaster;

namespace PalworldServerManager.SelfTest;

internal static class WindowsLanAvailabilityTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static void Check(bool value) { if (!value) throw new Exception("Windows discovery availability assertion failed."); }
    private static SocketException NetworkDown() => new((int)SocketError.NetworkDown);
    private static async Task<Exception> Failure(Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch (Exception ex) when (ex is not TimeoutException) { return ex; }
        throw new Exception("Expected discovery failure was not observed.");
    }
    private static void Released(int port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
    }
    private sealed class BrokenDisposeSocket : Socket
    {
        internal BrokenDisposeSocket() : base(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { }
        protected override void Dispose(bool disposing)
        { base.Dispose(disposing); if (disposing) throw new IOException("fixture socket cleanup failure"); }
    }
    public static async Task ExactOriginsAndSocketSetupCleanup()
    {
        var temporary = new[] { SocketError.NetworkDown, SocketError.NetworkUnreachable, SocketError.NetworkReset, SocketError.HostDown,
            SocketError.HostUnreachable, SocketError.ConnectionAborted, SocketError.ConnectionReset, SocketError.TimedOut,
            SocketError.AddressNotAvailable, SocketError.NoBufferSpaceAvailable, SocketError.SystemNotReady };
        foreach (var code in Enum.GetValues<SocketError>()) Check(Availability.IsTemporary(new SocketException((int)code)) == temporary.Contains(code));
        foreach (var code in new[] { 232, 1228, 0, 5, 8, 50, 87, 111, 12345 })
        {
            var original = new NetworkInformationException(code);
            Check(Availability.IsTemporary(original) == (code is 232 or 1228));
            var failure = await Failure(Task.Run(() => Availability.Invoke<int>(() => throw original)));
            Check(code is 232 or 1228 ? failure is LanDiscoveryUnavailableException && ReferenceEquals(failure.InnerException, original) : ReferenceEquals(failure, original));
        }
        Check(!Availability.IsTemporary(new AggregateException(NetworkDown())) && !Availability.IsTemporary(new OperationCanceledException()) &&
            !Availability.IsTemporary(new LanDiscoveryUnavailableException(NetworkDown())) && !Availability.IsTemporary(new IOException()));
        foreach (var cleanupFailure in new[] { false, true })
        {
            var port = 0; var original = NetworkDown();
            var failure = await Failure(Task.Run(() => Availability.CreateSocket(
                () => cleanupFailure ? new BrokenDisposeSocket() : new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp), socket =>
                { socket.ExclusiveAddressUse = true; socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); port = ((IPEndPoint)socket.LocalEndPoint!).Port; throw original; })));
            Check(port > 0); Released(port);
            if (cleanupFailure) Check(failure is AggregateException aggregate && aggregate.InnerExceptions.Contains(original) && failure.ToString().Contains("fixture socket cleanup failure"));
            else Check(failure is LanDiscoveryUnavailableException && ReferenceEquals(failure.InnerException, original));
        }
        var denied = new SocketException((int)SocketError.AccessDenied);
        Check(ReferenceEquals(await Failure(Task.Run(() => Availability.CreateSocket(() => throw denied, _ => { }))), denied));
        var asynchronous = NetworkDown();
        var wrapped = await Failure(Availability.InvokeAsync(() => ValueTask.FromException<int>(asynchronous)).AsTask());
        Check(wrapped is LanDiscoveryUnavailableException && ReferenceEquals(wrapped.InnerException, asynchronous));
    }

    private static async Task Send(Socket sender, int port) =>
        Check(await sender.SendToAsync(new byte[] { 1 }.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port)) == 1);
    public static async Task ReceiverKeepsCallbackAndCleanupFailuresFatal()
    {
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        foreach (var original in new Exception[] { NetworkDown(), new LanDiscoveryUnavailableException(NetworkDown()) })
        {
            var receiver = new WindowsLanDiscoveryReceiver((_, _, _, _) => throw original, (_, _) => true);
            try
            {
                await Send(sender, receiver.Port); var failure = await Failure(receiver.Completion);
                Check(failure is not LanDiscoveryUnavailableException);
                Check(original is LanDiscoveryUnavailableException ? failure is IOException && ReferenceEquals(failure.InnerException, original) : ReferenceEquals(failure, original));
                Check(ReferenceEquals(await Failure(receiver.DisposeAsync().AsTask()), failure)); Released(receiver.Port);
            }
            finally { try { await receiver.DisposeAsync(); } catch (Exception) { /* Expected injected callback failure already asserted. */ } }
        }
        foreach (var cleanupFailure in new[] { false, true })
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var reads = 0;
            CancellationTokenRegistration registration = default;
            var receiver = new WindowsLanDiscoveryReceiver((_, _, _, token) =>
            {
                if (cleanupFailure) registration = token.Register(() => throw new IOException("fixture cancellation cleanup failure"));
                ready.TrySetResult(); return ValueTask.CompletedTask;
            }, (_, _) => ++reads == 1 || Availability.Invoke<bool>(() => throw NetworkDown()));
            try
            {
                await Send(sender, receiver.Port); await ready.Task.WaitAsync(Deadline); await Send(sender, receiver.Port);
                var failure = await Failure(receiver.Completion);
                if (cleanupFailure) Check(failure is AggregateException && failure.ToString().Contains("fixture cancellation cleanup failure"));
                else Check(failure is LanDiscoveryUnavailableException && failure.InnerException is SocketException);
                Check(ReferenceEquals(await Failure(receiver.DisposeAsync().AsTask()), failure)); Released(receiver.Port);
            }
            finally { try { await receiver.DisposeAsync(); } catch (Exception) { /* Expected injected failure already asserted. */ } registration.Dispose(); }
        }
    }

    private sealed class FaultSender(Exception? failure, Exception? cleanupFailure) : Broadcaster.IDatagramSender
    {
        internal bool Disposed;
        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> payload, IPEndPoint destination, CancellationToken token) =>
            failure is null ? ValueTask.FromResult(payload.Length) : ValueTask.FromException<int>(failure);
        public void Dispose() { Disposed = true; if (cleanupFailure is not null) throw cleanupFailure; }
    }
    public static async Task RoundRequiresCleanOwnedTemporaryFailure()
    {
        var link = new LanDiscoveryLink(42, Guid.NewGuid(), 7, IPAddress.Parse("192.0.2.5"), 24);
        foreach (var cleanupFailure in new[] { false, true })
        {
            var temporary = await Failure(Availability.InvokeAsync<int>(() => throw NetworkDown()).AsTask());
            var sender = new FaultSender(temporary, cleanupFailure ? new IOException("fixture sender cleanup failure") : null);
            var failure = await Failure(Broadcaster.RoundAsync(45678, new byte[] { 1 }, () => new[] { link }, _ => sender, CancellationToken.None).AsTask());
            Check(sender.Disposed);
            if (cleanupFailure) Check(failure is AggregateException aggregate && aggregate.InnerExceptions.Contains(temporary) && failure.ToString().Contains("fixture sender cleanup failure"));
            else Check(ReferenceEquals(failure, temporary));
        }
        var raw = NetworkDown(); var unknownOrigin = new FaultSender(raw, null);
        Check(ReferenceEquals(await Failure(Broadcaster.RoundAsync(45678, new byte[] { 1 }, () => new[] { link }, _ => unknownOrigin, CancellationToken.None).AsTask()), raw));
        Check(unknownOrigin.Disposed);
        var labelledCleanup = new LanDiscoveryUnavailableException(NetworkDown());
        var brokenCleanup = new FaultSender(null, labelledCleanup);
        var cleanupResult = await Failure(Broadcaster.RoundAsync(45678, new byte[] { 1 }, () => new[] { link }, _ => brokenCleanup, CancellationToken.None).AsTask());
        Check(brokenCleanup.Disposed && cleanupResult is AggregateException fatal && fatal.InnerExceptions.Contains(labelledCleanup));
    }
}
