using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PalworldServerManager.Platform.Contracts;
using Sender = PalworldServerManager.Platform.Windows.WindowsLanDiscoveryBroadcaster;

namespace PalworldServerManager.SelfTest;

internal static class WindowsLanBroadcasterTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static void Check(bool value) { if (!value) throw new Exception("Windows discovery broadcaster assertion failed."); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static LanDiscoveryLink Link() => new(42, Guid.NewGuid(), 7, IPAddress.Parse("192.0.2.5"), 24);
    private sealed class FakeSender : Sender.IDatagramSender
    {
        internal bool Disposed; internal int Sends;
        internal Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask<int>> OnSend = (data, _, _) => ValueTask.FromResult(data.Length);
        internal Action OnDispose = () => { };
        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> data, IPEndPoint destination, CancellationToken token)
        { Check(!Disposed); Sends++; return OnSend(data, destination, token); }
        public void Dispose() { Disposed = true; OnDispose(); }
    }
    private static async Task Failure(Task task, string message)
    {
        try { await task.WaitAsync(Deadline); }
        catch (Exception ex) when (ex is not TimeoutException && ex.ToString().Contains(message, StringComparison.Ordinal)) { return; }
        throw new Exception("Required broadcaster failure was not observed.");
    }
    private static async Task Cancelled(Task task)
    {
        try { await task.WaitAsync(Deadline); } catch (OperationCanceledException) { return; }
        throw new Exception("Discovery broadcast cancellation was not preserved.");
    }

    public static async Task BoundedFreshRoundAndPublicPacket()
    {
        var link = Link(); var original = new byte[] { 71, 72 }; var reads = 0; var opens = 0;
        var fake = new FakeSender { OnSend = (data, endpoint, _) =>
        { Check(data.Span.SequenceEqual(new byte[] { 71, 72 }) && endpoint.Address.Equals(link.BroadcastAddress) && endpoint.Port == 45678); return ValueTask.FromResult(data.Length); } };
        var limited = new LanDiscoveryLink(42, link.InterfaceId, 7, IPAddress.Parse("192.0.2.5"), 1);
        var loopbackBroadcast = new LanDiscoveryLink(42, link.InterfaceId, 7, IPAddress.Parse("10.0.0.5"), 1);
        var tooLargeIndex = new LanDiscoveryLink(42, link.InterfaceId, 0x01000000, link.LocalAddress, 24);
        Check(limited.BroadcastAddress.Equals(IPAddress.Broadcast) && !Sender.CanSend(limited) && !Sender.CanSend(loopbackBroadcast) && !Sender.CanSend(tooLargeIndex));
        var count = await Sender.RoundAsync(45678, original, () =>
        {
            reads++;
            // The real native reader reconstructs records on every observation.
            var reconstructed = new LanDiscoveryLink(42, link.InterfaceId, 7, IPAddress.Parse("192.0.2.5"), 24);
            Check(!ReferenceEquals(link, reconstructed));
            return reads == 1 ? new[] { link, reconstructed, limited, loopbackBroadcast, tooLargeIndex } : new[] { reconstructed };
        },
            _ => { opens++; original[0] = 99; return fake; }, CancellationToken.None);
        Check(count == 1 && opens == 1 && reads == 2 && fake.Disposed && fake.Sends == 1);

        foreach (var changed in new IReadOnlyList<LanDiscoveryLink>[] { [],
            [new(43, link.InterfaceId, 7, link.LocalAddress, 24)], [new(42, Guid.NewGuid(), 7, link.LocalAddress, 24)],
            [new(42, link.InterfaceId, 8, link.LocalAddress, 24)], [new(42, link.InterfaceId, 7, IPAddress.Parse("192.0.2.6"), 24)],
            [new(42, link.InterfaceId, 7, link.LocalAddress, 25)] })
        {
            reads = 0; var opened = false; fake = new();
            Check(await Sender.RoundAsync(45678, new byte[] { 1 }, () => { reads++; if (reads == 1) return new[] { link }; Check(opened); return changed; },
                _ => { opened = true; return fake; }, CancellationToken.None) == 0);
            Check(fake.Disposed && fake.Sends == 0 && reads == 2);
        }
        var many = Enumerable.Range(1, 400).Select(i => new LanDiscoveryLink(42, link.InterfaceId, 7,
            new IPAddress(new byte[] { 10, 2, (byte)(i >> 8), (byte)i }), 16)).ToArray();
        var resident = 0; opens = 0;
        Check(await Sender.RoundAsync(45678, new byte[] { 1 }, () => many, _ =>
        { Check(resident == 0); resident++; opens++; return new FakeSender { OnDispose = () => resident-- }; }, CancellationToken.None) == 256);
        Check(opens == 256 && resident == 0);
    }

    public static async Task FailureCancellationAndOwnedCleanup()
    {
        var link = Link();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var reads = 0;
            await Cancelled(Sender.RoundAsync(45678, new byte[] { 1 }, () => { reads++; return new[] { link }; }, _ => throw new Exception("Unexpected sender creation."), cancelled.Token).AsTask());
            Check(reads == 0);
        }
        foreach (var mode in new[] { "refresh", "send", "short", "dispose", "both" })
        {
            var reads = 0; var fake = new FakeSender
            {
                OnSend = (data, _, _) => mode is "send" or "both" ? throw new IOException("fixture send failure") : ValueTask.FromResult(mode == "short" ? 0 : data.Length),
                OnDispose = () => { if (mode is "dispose" or "both") throw new IOException("fixture dispose failure"); }
            };
            var task = Sender.RoundAsync(45678, new byte[] { 1 }, () =>
            { if (++reads == 2 && mode == "refresh") throw new IOException("fixture refresh failure"); return new[] { link }; }, _ => fake, CancellationToken.None).AsTask();
            await Failure(task, mode switch { "refresh" => "fixture refresh failure", "short" => "not accepted in full", "dispose" => "fixture dispose failure", _ => "fixture send failure" });
            if (mode == "both") await Failure(task, "fixture dispose failure");
            Check(fake.Disposed && fake.Sends == (mode == "refresh" ? 0 : 1));
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var fake = new FakeSender();
            await Cancelled(Sender.RoundAsync(45678, new byte[] { 1 }, () => new[] { link }, _ => { cancellation.Cancel(); return fake; }, cancellation.Token).AsTask());
            Check(fake.Disposed && fake.Sends == 0);
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var entered = Signal(); var release = Signal(); var fake = new FakeSender
            { OnSend = async (data, _, token) => { entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested(); return data.Length; } };
            var round = Sender.RoundAsync(45678, new byte[] { 1 }, () => new[] { link }, _ => fake, cancellation.Token).AsTask();
            try
            {
                await entered.Task.WaitAsync(Deadline); cancellation.Cancel(); Check(!round.IsCompleted && !fake.Disposed);
                release.TrySetResult(); await Cancelled(round); Check(fake.Disposed);
            }
            finally { release.TrySetResult(); await Cancelled(round); }
        }
        foreach (var size in new[] { 0, 513 })
        {
            var reads = 0;
            await Failure(Sender.RoundAsync(45678, new byte[size], () => { reads++; return new[] { link }; }, _ => new FakeSender(), CancellationToken.None).AsTask(), "bounded public discovery packet");
            Check(reads == 0);
        }
        foreach (var port in new[] { -1, 0, 65536 })
        {
            try { _ = new Sender(port); } catch (ArgumentOutOfRangeException) { continue; }
            throw new Exception("Invalid broadcaster port accepted.");
        }
    }

    public static async Task ActualWindowsSocketConstraints()
    {
        using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0)); var destination = (IPEndPoint)receiver.LocalEndPoint!;
        var index = NetworkInterface.LoopbackInterfaceIndex;
        using var sender = new Sender.DatagramSender(IPAddress.Loopback, index);
        Check(sender.Configuration == new Sender.SocketConfiguration(index, 1, true, true, true));
        var payload = Enumerable.Repeat((byte)73, 512).ToArray();
        Check(await sender.SendAsync(payload, destination, CancellationToken.None) == 512);
        var buffer = new byte[513];
        using var deadline = new CancellationTokenSource(Deadline);
        var packet = await receiver.ReceiveMessageFromAsync(buffer.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), deadline.Token);
        Check(packet.ReceivedBytes == 512 && buffer.AsSpan(0, 512).SequenceEqual(payload) && packet.PacketInformation.Interface == index &&
            packet.RemoteEndPoint is IPEndPoint source && source.Address.Equals(IPAddress.Loopback) && source.Port == sender.SourcePort);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Cancelled(sender.SendAsync(new byte[] { 74 }, destination, cancelled.Token).AsTask());
        Check(await sender.SendAsync(new byte[] { 75 }, destination, CancellationToken.None) == 1);
        packet = await receiver.ReceiveMessageFromAsync(buffer.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), deadline.Token);
        Check(packet.ReceivedBytes == 1 && buffer[0] == 75);
        sender.Dispose();
        using var rebind = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        rebind.Bind(new IPEndPoint(IPAddress.Any, sender.SourcePort));
        foreach (var invalid in new[] { 0, -1, 0x01000000 })
        {
            try { using var bad = new Sender.DatagramSender(IPAddress.Loopback, invalid); }
            catch (ArgumentException) { continue; }
            throw new Exception("Invalid outgoing interface accepted.");
        }
    }
}
