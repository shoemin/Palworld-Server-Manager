using Google.Protobuf;
using PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.Contracts;

public sealed record UnverifiedHostAdvertisement(Guid ClaimedHostId, uint ProtocolMajor, uint ProtocolMinor, int PeerPort, int PairingPort);

public static class HostDiscoveryCodec
{
    public const int MaximumPacketBytes = 512;
    private const string Marker = "palworld.manager.host.discovery.v1";
    public static byte[] Encode(UnverifiedHostAdvertisement advertisement)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        if (!Valid(advertisement)) throw new ArgumentException("Invalid Host discovery metadata.");
        return new HostDiscoveryAdvertisement { Marker = Marker, ClaimedHostId = advertisement.ClaimedHostId.ToString("D"),
            Protocol = new() { Major = advertisement.ProtocolMajor, Minor = advertisement.ProtocolMinor },
            PeerPort = (uint)advertisement.PeerPort, PairingPort = (uint)advertisement.PairingPort }.ToByteArray();
    }
    public static UnverifiedHostAdvertisement? Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length is 0 or > MaximumPacketBytes) return null;
        try
        {
            var value = HostDiscoveryAdvertisement.Parser.ParseFrom(packet);
            if (value.Marker != Marker || !Guid.TryParseExact(value.ClaimedHostId, "D", out var id) || value.Protocol is null ||
                value.PeerPort > 65535 || value.PairingPort > 65535) return null;
            var result = new UnverifiedHostAdvertisement(id, value.Protocol.Major, value.Protocol.Minor, (int)value.PeerPort, (int)value.PairingPort);
            return Valid(result) ? result : null;
        }
        catch (InvalidProtocolBufferException) { return null; }
    }
    private static bool Valid(UnverifiedHostAdvertisement value) => value.ClaimedHostId != Guid.Empty && value.ProtocolMajor > 0 &&
        value.PeerPort is >= 1 and <= 65535 && value.PairingPort is >= 1 and <= 65535;
}
