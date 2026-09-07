using System.Net;
using PalworldServerManager.Contracts;

namespace PalworldServerManager.Host;

internal sealed record UnverifiedHostEndpoint(UnverifiedHostAdvertisement Advertisement,
    HostReachableAddress PeerAddress, HostReachableAddress PairingAddress);

// Trusted composition supplies only actual received sources admitted by its LAN policy.
// This cache neither opens a socket nor decides that an interface/source is a physical LAN.
internal sealed class HostDiscoveryDirectory
{
    internal const int MaximumEntries = 256;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(12);
    private readonly Guid self;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<(Guid Host, string Source, int Peer, int Pairing), (UnverifiedHostEndpoint Endpoint, long Seen)> entries = [];
    internal HostDiscoveryDirectory(Guid self, TimeProvider? time = null)
    { this.self = self != Guid.Empty ? self : throw new ArgumentException("Host identity required."); this.time = time ?? TimeProvider.System; }

    internal bool Observe(IPAddress actualSource, ReadOnlySpan<byte> packet)
    {
        ArgumentNullException.ThrowIfNull(actualSource);
        var advertisement = HostDiscoveryCodec.Decode(packet);
        if (advertisement is null || advertisement.ClaimedHostId == self) return false;
        if (actualSource.IsIPv4MappedToIPv6) actualSource = actualSource.MapToIPv4();
        HostReachableAddress peer, pairing;
        try
        {
            peer = HostReachableAddress.Parse(actualSource.ToString(), advertisement.PeerPort);
            pairing = HostReachableAddress.Parse(actualSource.ToString(), advertisement.PairingPort);
        }
        catch (ArgumentException) { return false; }
        lock (gate)
        {
            Sweep(); var key = (advertisement.ClaimedHostId, peer.Host, peer.Port, pairing.Port);
            if (!entries.ContainsKey(key) && entries.Count >= MaximumEntries) return false;
            entries[key] = (new(advertisement, peer, pairing), time.GetTimestamp()); return true;
        }
    }
    internal IReadOnlyList<UnverifiedHostEndpoint> Snapshot()
    {
        lock (gate)
        {
            Sweep(); return entries.Values.Select(value => value.Endpoint).OrderBy(value => value.Advertisement.ClaimedHostId)
                .ThenBy(value => value.PeerAddress.Host, StringComparer.Ordinal).ThenBy(value => value.PeerAddress.Port)
                .ThenBy(value => value.PairingAddress.Port).ToArray();
        }
    }
    private void Sweep()
    {
        foreach (var key in entries.Where(value => time.GetElapsedTime(value.Value.Seen) >= Lifetime).Select(value => value.Key).ToArray()) entries.Remove(key);
    }
}
