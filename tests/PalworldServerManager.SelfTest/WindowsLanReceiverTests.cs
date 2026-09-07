using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static class WindowsLanReceiverTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new Exception("Windows discovery receiver assertion failed."); }
    private static Socket Udp()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        try { socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); return socket; }
        catch { socket.Dispose(); throw; }
    }
    private static async Task Send(Socket socket, int port, byte[] payload) =>
        Check(await socket.SendToAsync(payload.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port)) == payload.Length);
    private static void RequireReleased(int port)
    {
        // Real exclusive rebinding proves socket closure; elapsed time cannot do that.
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        probe.Bind(new IPEndPoint(IPAddress.Any, port));
    }
    private static async Task RequireFailure(Task task, string message)
    {
        try { await task.WaitAsync(Deadline); }
        catch (Exception ex) when (ex is not TimeoutException && ex.ToString().Contains(message, StringComparison.Ordinal)) { return; }
        throw new Exception("Expected discovery receiver failure was not preserved.");
    }

    public static async Task ActualPacketsAndFreshAdmission()
    {
        var messages = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(); var rejected = Signal();
        var allow = 1; var admissions = 0; var callbacks = 0;
        await using var receiver = new WindowsLanDiscoveryReceiver((index, source, payload, _) =>
        {
            Check(index == NetworkInterface.LoopbackInterfaceIndex && source.Equals(IPAddress.Loopback));
            Interlocked.Increment(ref callbacks); Check(messages.Writer.TryWrite(payload)); return ValueTask.CompletedTask;
        }, (index, source) =>
        {
            Check(index == NetworkInterface.LoopbackInterfaceIndex && source.Equals(IPAddress.Loopback));
            Interlocked.Increment(ref admissions);
            if (Volatile.Read(ref allow) == 0) { rejected.TrySetResult(); return false; }
            return true;
        });
        using var sender = Udp();
        await Send(sender, receiver.Port, []);
        await Send(sender, receiver.Port, new byte[513]);
        await Send(sender, receiver.Port, new byte[4096]);
        var original = Enumerable.Repeat((byte)71, 512).ToArray();
        await Send(sender, receiver.Port, original);
        var first = await messages.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Check(first.Span.SequenceEqual(original) && Volatile.Read(ref callbacks) == 1 && Volatile.Read(ref admissions) == 1);
        Volatile.Write(ref allow, 0); await Send(sender, receiver.Port, [72]); await rejected.Task.WaitAsync(Deadline);
        Volatile.Write(ref allow, 1); await Send(sender, receiver.Port, [73]);
        var second = await messages.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Check(second.Span.SequenceEqual(new byte[] { 73 }) && first.Span.SequenceEqual(original));
        Check(Volatile.Read(ref callbacks) == 2 && Volatile.Read(ref admissions) == 3);
        await receiver.DisposeAsync(); RequireReleased(receiver.Port);
    }

    public static async Task StopDrainsSerializedCallback()
    {
        var entered = Signal(); var cancelled = Signal(); var release = Signal(); var calls = 0;
        var receiver = new WindowsLanDiscoveryReceiver(async (_, _, _, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task;
        }, (_, _) => true);
        try
        {
            using var sender = Udp(); await Send(sender, receiver.Port, [1]); await entered.Task.WaitAsync(Deadline);
            await Send(sender, receiver.Port, [2]);
            var firstStop = receiver.DisposeAsync().AsTask(); var secondStop = receiver.DisposeAsync().AsTask();
            await cancelled.Task.WaitAsync(Deadline);
            Check(!firstStop.IsCompleted && !secondStop.IsCompleted && !receiver.Completion.IsCompleted && Volatile.Read(ref calls) == 1);
            release.TrySetResult(); await Task.WhenAll(firstStop, secondStop).WaitAsync(Deadline);
            Check(Volatile.Read(ref calls) == 1 && receiver.Completion.IsCompletedSuccessfully); RequireReleased(receiver.Port);
        }
        finally { release.TrySetResult(); await receiver.DisposeAsync(); }
    }

    public static async Task WorkerAndCancellationFailures()
    {
        foreach (var failAdmission in new[] { false, true })
        {
            var receiver = new WindowsLanDiscoveryReceiver((_, _, _, _) => throw new IOException("fixture callback failure"),
                (_, _) => failAdmission ? throw new IOException("fixture admission failure") : true);
            var marker = failAdmission ? "fixture admission failure" : "fixture callback failure";
            try
            {
                using var sender = Udp(); await Send(sender, receiver.Port, [1]);
                await RequireFailure(receiver.Completion, marker); await RequireFailure(receiver.DisposeAsync().AsTask(), marker);
                RequireReleased(receiver.Port);
            }
            finally
            {
                try { await receiver.DisposeAsync(); }
                catch (IOException ex) when (ex.Message == marker) { }
            }
        }
        var entered = Signal(); var cancelled = Signal(); var release = Signal();
        var failingCancellation = new WindowsLanDiscoveryReceiver(async (_, _, _, token) =>
        {
            using var registration = token.Register(() => { cancelled.TrySetResult(); throw new IOException("fixture cancellation failure"); });
            entered.TrySetResult(); await release.Task; throw new IOException("fixture simultaneous worker failure");
        }, (_, _) => true);
        try
        {
            using var sender = Udp(); await Send(sender, failingCancellation.Port, [1]); await entered.Task.WaitAsync(Deadline);
            var drain = failingCancellation.DisposeAsync().AsTask(); await cancelled.Task.WaitAsync(Deadline);
            Check(!drain.IsCompleted); release.TrySetResult();
            await RequireFailure(drain, "fixture cancellation failure");
            await RequireFailure(drain, "fixture simultaneous worker failure"); RequireReleased(failingCancellation.Port);
        }
        finally
        {
            release.TrySetResult();
            await RequireFailure(failingCancellation.DisposeAsync().AsTask(), "fixture cancellation failure");
        }
    }

    public static async Task ExplicitPortAndExclusiveOwnership()
    {
        try { await using var invalid = new WindowsLanDiscoveryReceiver(49152, null!); throw new Exception("Null callback accepted."); }
        catch (ArgumentNullException) { }
        foreach (var port in new[] { -1, 0, 65536 })
        {
            try { await using var invalid = new WindowsLanDiscoveryReceiver(port, (_, _, _, _) => ValueTask.CompletedTask); }
            catch (ArgumentOutOfRangeException) { continue; }
            throw new Exception("Invalid production discovery port accepted.");
        }
        var occupied = Udp(); var boundPort = ((IPEndPoint)occupied.LocalEndPoint!).Port;
        try
        {
            try { await using var duplicate = new WindowsLanDiscoveryReceiver(boundPort, (_, _, _, _) => ValueTask.CompletedTask); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied) { occupied.Dispose(); occupied = null!; }
            Check(occupied is null);
        }
        finally { occupied?.Dispose(); }
        // Actual production binding with the fixed native policy; no physical LAN packet is sent.
        await using var receiver = new WindowsLanDiscoveryReceiver(boundPort, (_, _, _, _) => throw new Exception("Unexpected production packet callback."));
        Check(receiver.Port == boundPort); await receiver.DisposeAsync(); RequireReleased(boundPort);
    }
}
