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
        RoundAsync(port, publicPacket, WindowsLanAvailability.ReadLinks,
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
            catch (Exception ex) { failure = failure is null ? new AggregateException("Discovery sender cleanup failed.", ex) : new AggregateException(failure, ex); }
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
        internal SocketConfiguration Configuration => ReadConfiguration(socket);
        private static SocketConfiguration ReadConfiguration(Socket socket) => new(Read(socket, SocketOptionLevel.IP, (SocketOptionName)UnicastInterfaceOption),
            Read(socket, SocketOptionLevel.IP, SocketOptionName.IpTimeToLive), Read(socket, SocketOptionLevel.Socket, SocketOptionName.DontRoute) != 0,
            Read(socket, SocketOptionLevel.Socket, SocketOptionName.Broadcast) != 0, Read(socket, SocketOptionLevel.IP, SocketOptionName.DontFragment) != 0);
        internal DatagramSender(IPAddress source, int index)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.AddressFamily != AddressFamily.InterNetwork || source.Equals(IPAddress.Any) || source.GetAddressBytes()[0] is 0 or >= 224 || index is < 1 or > 0x00ffffff)
                throw new ArgumentException("An explicit IPv4 source and interface are required.");
            var sourcePort = 0;
            socket = WindowsLanAvailability.CreateSocket(() => new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp), candidate =>
            {
                candidate.ExclusiveAddressUse = true;
                candidate.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)UnicastInterfaceOption, IPAddress.HostToNetworkOrder(index));
                candidate.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, 1);
                candidate.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontRoute, true);
                candidate.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                candidate.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DontFragment, true);
                candidate.Bind(new IPEndPoint(new IPAddress(source.GetAddressBytes()), 0));
                if (ReadConfiguration(candidate) != new SocketConfiguration(index, 1, true, true, true))
                    throw new IOException("Required LAN discovery send constraints are unavailable.");
                sourcePort = ((IPEndPoint)candidate.LocalEndPoint!).Port;
            });
            SourcePort = sourcePort;
        }
        private static int Read(Socket socket, SocketOptionLevel level, SocketOptionName name) => Convert.ToInt32(socket.GetSocketOption(level, name));
        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> payload, IPEndPoint destination, CancellationToken cancellationToken) =>
            WindowsLanAvailability.InvokeAsync(() => socket.SendToAsync(payload, SocketFlags.None, destination, cancellationToken));
        public void Dispose() => socket.Dispose();
    }
}
