using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace PalworldServerManager.Platform.Contracts;

// Current platform observation only. Interface indices are not durable identities or trust.
public sealed record LanDiscoveryLink
{
    public ulong InterfaceLuid { get; }
    public Guid InterfaceId { get; }
    public int InterfaceIndex { get; }
    public int PrefixLength { get; }
    private readonly uint local, network, broadcast;
    public IPAddress LocalAddress => Address(local);
    public IPAddress BroadcastAddress => Address(broadcast);
    public LanDiscoveryLink(ulong interfaceLuid, Guid interfaceId, int interfaceIndex, IPAddress localAddress, int prefixLength)
    {
        if (interfaceLuid == 0 || interfaceId == Guid.Empty || interfaceIndex <= 0 || prefixLength is < 1 or > 30 || !Unicast(localAddress))
            throw new ArgumentException("A current IPv4 LAN interface and host subnet are required.");
        local = Number(localAddress); var mask = uint.MaxValue << (32 - prefixLength);
        network = local & mask; broadcast = network | ~mask;
        if (local == network || local == broadcast) throw new ArgumentException("The local address must identify a subnet host.");
        InterfaceLuid = interfaceLuid; InterfaceId = interfaceId; InterfaceIndex = interfaceIndex; PrefixLength = prefixLength;
    }
    public bool AdmitsSource(int actualInterfaceIndex, IPAddress actualSource)
    {
        if (actualInterfaceIndex != InterfaceIndex || !Unicast(actualSource)) return false;
        var source = Number(actualSource);
        return source > network && source < broadcast && source != local;
    }
    private static bool Unicast(IPAddress? address) => address is not null && address.AddressFamily == AddressFamily.InterNetwork &&
        address.GetAddressBytes()[0] is > 0 and < 224 && !IPAddress.IsLoopback(address);
    private static uint Number(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
    private static IPAddress Address(uint value)
    { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); return new(bytes); }
}

public interface ILanDiscoveryInterfaceSource
{
    // Fresh observation; callers must not persist/cache it as authority over later network changes.
    IReadOnlyList<LanDiscoveryLink> Read();
}
