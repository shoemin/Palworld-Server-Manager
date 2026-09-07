using System.Net;
using System.Net.Sockets;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Platform.Windows;

public sealed class WindowsLanDiscoveryReceiver : ILanDiscoveryReceiver
{
    internal const int MaximumPacketBytes = 512;
    private static readonly TimeSpan ReceiveInterval = TimeSpan.FromMilliseconds(10);
    private readonly Socket socket;
    private readonly CancellationTokenSource stopping;
    private readonly CancellationToken token;
    private readonly object gate = new();
    private readonly Func<int, IPAddress, bool> admits;
    private readonly LanDiscoveryReceived received;
    private Task? stopTask;
    private bool stopRequested;
    public int Port { get; }
    public Task Completion { get; }

    public WindowsLanDiscoveryReceiver(int port, LanDiscoveryReceived received)
        : this(ProductionEndpoint(port), received, ProductionAdmission) { }

    // Socket mechanics fixture only: cannot listen on any non-loopback address.
    internal WindowsLanDiscoveryReceiver(LanDiscoveryReceived received, Func<int, IPAddress, bool> admits)
        : this(new IPEndPoint(IPAddress.Loopback, 0), received, admits) { }

    private WindowsLanDiscoveryReceiver(IPEndPoint endpoint, LanDiscoveryReceived received, Func<int, IPAddress, bool> admits)
    {
        ArgumentNullException.ThrowIfNull(received); ArgumentNullException.ThrowIfNull(admits);
        this.received = received; this.admits = admits;
        var actualPort = 0;
        socket = WindowsLanAvailability.CreateSocket(() => new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp), candidate =>
        {
            candidate.ExclusiveAddressUse = true;
            candidate.ReceiveBufferSize = 64 * 1024;
            candidate.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            if (Convert.ToInt32(candidate.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation)) == 0)
                throw new IOException("IPv4 packet information is required for LAN discovery.");
            candidate.Bind(endpoint); actualPort = ((IPEndPoint)candidate.LocalEndPoint!).Port;
        });
        Port = actualPort; stopping = new(); token = stopping.Token;
        try { Completion = Task.Run(RunAsync); }
        catch { try { socket.Dispose(); } finally { stopping.Dispose(); } throw; }
    }

    private static IPEndPoint ProductionEndpoint(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return new(IPAddress.Any, port);
    }
    private static bool ProductionAdmission(int index, IPAddress source) =>
        WindowsLanAvailability.ReadLinks().Any(link => link.AdmitsSource(index, source));

    private async Task RunAsync()
    {
        Exception? failure = null;
        try
        {
            var buffer = new byte[MaximumPacketBytes + 1];
            EndPoint anySource = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                // Even malformed/oversize traffic pays this interval; no cached eligibility authority.
                await Task.Delay(ReceiveInterval, token).ConfigureAwait(false);
                SocketReceiveMessageFromResult packet;
                try { packet = await WindowsLanAvailability.InvokeAsync(() => socket.ReceiveMessageFromAsync(buffer.AsMemory(), SocketFlags.None, anySource, token)).ConfigureAwait(false); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize) { continue; }
                catch (Exception ex) when (IsStopping() && ex is SocketException or ObjectDisposedException) { break; }
                if (packet.ReceivedBytes is < 1 or > MaximumPacketBytes || (packet.SocketFlags & (SocketFlags.Truncated | SocketFlags.ControlDataTruncated)) != 0 ||
                    packet.PacketInformation.Interface <= 0 || packet.RemoteEndPoint is not IPEndPoint source || source.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (!admits(packet.PacketInformation.Interface, source.Address)) continue;
                // Admission is serialized with stop. An already admitted callback belongs to the drain.
                lock (gate) { if (stopRequested) break; }
                try
                {
                    await received(packet.PacketInformation.Interface, new IPAddress(source.Address.GetAddressBytes()),
                        buffer.AsMemory(0, packet.ReceivedBytes).ToArray(), token).ConfigureAwait(false);
                }
                catch (LanDiscoveryUnavailableException ex)
                { throw new IOException("A discovery callback failed outside the Windows availability boundary.", ex); }
            }
        }
        catch (OperationCanceledException ex) when (IsStopping() && ex.CancellationToken == token) { }
        catch (Exception ex) { failure = ex; }
        try { await RequestStop().ConfigureAwait(false); }
        catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
        finally { stopping.Dispose(); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private bool IsStopping() { lock (gate) return stopRequested; }
    private Task RequestStop()
    {
        lock (gate)
        {
            stopRequested = true;
            return stopTask ??= CloseAsync();
        }
    }
    private async Task CloseAsync()
    {
        Exception? failure = null;
        try { socket.Dispose(); } catch (Exception ex) { failure = new AggregateException("Discovery receiver socket cleanup failed.", ex); }
        try { await stopping.CancelAsync().ConfigureAwait(false); }
        catch (Exception ex) { failure = failure is null ? new AggregateException("Discovery cancellation cleanup failed.", ex) : new AggregateException(failure, ex); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    public ValueTask DisposeAsync()
    {
        _ = RequestStop(); // The single worker awaits this same cleanup task and preserves its failure.
        return new(Completion);
    }
}
