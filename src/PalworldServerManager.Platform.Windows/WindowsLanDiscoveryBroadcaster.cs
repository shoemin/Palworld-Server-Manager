using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Platform.Windows;

public sealed class WindowsLanDiscoveryBroadcaster : ILanDiscoveryBroadcaster
{
    private const int UnicastInterfaceOption = 31; // Windows SDK IP_UNICAST_IF (ws2ipdef.h).
    private readonly int port;
    public WindowsLanDiscoveryBroadcaster(int port) { ValidatePort(port); this.port = port; }
    public ValueTask<int> BroadcastAsync(ReadOnlyMemory<byte> publicPacket, CancellationToken cancellationToken) =>
        RoundAsync(port, publicPacket, new WindowsLanInterfaceSource().Read,
            link => new DatagramSender(link.LocalAddress, link.InterfaceIndex), cancellationToken);

    internal interface IDatagramSender : IDisposable
    {
        ValueTask<int> SendAsync(ReadOnlyMemory<byte> payload, IPEndPoint destination, CancellationToken cancellationToken);
    }
    internal static bool CanSend(LanDiscoveryLink link) => link.InterfaceIndex is > 0 and <= 0x00ffffff &&
        !link.BroadcastAddress.Equals(IPAddress.Broadcast) && !IPAddress.IsLoopback(link.BroadcastAddress);

    internal static async ValueTask<int> RoundAsync(int port, ReadOnlyMemory<byte> packet,
        Func<IReadOnlyList<LanDiscoveryLink>> read, Func<LanDiscoveryLink, IDatagramSender> create, CancellationToken token)
    {
        ValidatePort(port); ArgumentNullException.ThrowIfNull(read); ArgumentNullException.ThrowIfNull(create);
        if (packet.Length is < 1 or > WindowsLanDiscoveryReceiver.MaximumPacketBytes)
            throw new ArgumentException("A bounded public discovery packet is required.", nameof(packet));
        token.ThrowIfCancellationRequested(); var ownedPacket = packet.ToArray();
        var links = read().Take(WindowsLanInterfaceSource.MaximumAddresses).Distinct().Where(CanSend).ToArray();
        var accepted = 0;
        foreach (var link in links)
        {
            token.ThrowIfCancellationRequested();
            IDatagramSender? sender = null; Exception? failure = null;
            try
            {
                sender = create(link); token.ThrowIfCancellationRequested();
                // Revalidate exact native identity, address and prefix AFTER opening/configuring the socket.
                if (read().Take(WindowsLanInterfaceSource.MaximumAddresses).Contains(link))
                {
                    token.ThrowIfCancellationRequested();
                    var bytes = await sender.SendAsync(ownedPacket, new IPEndPoint(link.BroadcastAddress, port), token).ConfigureAwait(false);
                    if (bytes != ownedPacket.Length) throw new IOException("The discovery datagram was not accepted in full.");
                    accepted++;
                }
            }
            catch (Exception ex) { failure = ex; }
            try { sender?.Dispose(); }
            catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        token.ThrowIfCancellationRequested(); return accepted;
    }
    private static void ValidatePort(int port)
    { if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port)); }

    internal readonly record struct SocketConfiguration(int InterfaceIndex, int Ttl, bool NoRoute, bool Broadcast, bool NoFragment);
    internal sealed class DatagramSender : IDatagramSender
    {
        private readonly Socket socket;
        internal int SourcePort { get; }
        internal SocketConfiguration Configuration => new(Read(SocketOptionLevel.IP, (SocketOptionName)UnicastInterfaceOption),
            Read(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive), Read(SocketOptionLevel.Socket, SocketOptionName.DontRoute) != 0,
            Read(SocketOptionLevel.Socket, SocketOptionName.Broadcast) != 0, Read(SocketOptionLevel.IP, SocketOptionName.DontFragment) != 0);
        internal DatagramSender(IPAddress source, int index)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.AddressFamily != AddressFamily.InterNetwork || source.Equals(IPAddress.Any) || source.GetAddressBytes()[0] is 0 or >= 224 || index is < 1 or > 0x00ffffff)
                throw new ArgumentException("An explicit IPv4 source and interface are required.");
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.ExclusiveAddressUse = true;
                socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)UnicastInterfaceOption, IPAddress.HostToNetworkOrder(index));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, 1);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontRoute, true);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DontFragment, true);
                socket.Bind(new IPEndPoint(new IPAddress(source.GetAddressBytes()), 0));
                if (Configuration != new SocketConfiguration(index, 1, true, true, true))
                    throw new IOException("Required LAN discovery send constraints are unavailable.");
                SourcePort = ((IPEndPoint)socket.LocalEndPoint!).Port;
            }
            catch { socket.Dispose(); throw; }
        }
        private int Read(SocketOptionLevel level, SocketOptionName name) => Convert.ToInt32(socket.GetSocketOption(level, name));
        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> payload, IPEndPoint destination, CancellationToken cancellationToken) =>
            socket.SendToAsync(payload, SocketFlags.None, destination, cancellationToken);
        public void Dispose() => socket.Dispose();
    }
}
