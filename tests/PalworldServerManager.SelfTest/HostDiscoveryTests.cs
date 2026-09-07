using System.Net;
using Google.Protobuf;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;

namespace PalworldServerManager.SelfTest;

internal static class HostDiscoveryTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Host discovery/address assertion failed."); }
    private static void Invalid(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid reachable address accepted."); }
    private sealed class Clock : TimeProvider
    {
        internal long Stamp;
        internal DateTimeOffset Utc = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Stamp;
        public override DateTimeOffset GetUtcNow() => Utc;
    }
    public static Task ManualAddresses()
    {
        var host = HostReachableAddress.Parse("Example.COM.", 443);
        Check(host.Host == "example.com" && host.Port == 443 && host.HttpsAddress == new Uri("https://example.com/"));
        Check(HostReachableAddress.Parse("bücher.example", 5012).Host == "xn--bcher-kva.example");
        Check(HostReachableAddress.Parse("[2001:db8::1]", 5012).HttpsAddress == new Uri("https://[2001:db8::1]:5012/"));
        Check(HostReachableAddress.Parse("::ffff:192.0.2.1", 65535).HttpsAddress == new Uri("https://192.0.2.1:65535/"));
        Check(HostReachableAddress.Parse("localhost", 1).Host == "localhost");
        foreach (var text in new[] { "", " example.com", "example.com ", "a b", "https://example.com", "a@b", "a/b", "a\\b", "a?b", "a#b",
            "example.com:443", "[example.com]", "[127.0.0.1]", "[::1", "::1]", "-a", "a-", "a..b", "a_b", "a\n", "0.0.0.0", "::", "::ffff:0.0.0.0",
            "224.0.0.1", "::ffff:224.0.0.1", "255.255.255.255", "ff02::1", "fe80::1%3", "fe80::1%0", "::ffff:192.0.2.1%3",
            "[fe80::1%3]", "fe80::1%not-an-interface", "０.０.０.０", new string('a', 64) + ".example" })
            Invalid(() => HostReachableAddress.Parse(text, 5000));
        foreach (var port in new[] { -1, 0, 65536, int.MaxValue }) Invalid(() => HostReachableAddress.Parse("example.com", port));
        return Task.CompletedTask;
    }
    public static Task PacketBoundsAndCompatibility()
    {
        var metadata = new UnverifiedHostAdvertisement(Guid.NewGuid(), 1, 7, 5000, 5001);
        var packet = HostDiscoveryCodec.Encode(metadata);
        Check(packet.Length < HostDiscoveryCodec.MaximumPacketBytes && HostDiscoveryCodec.Decode(packet) == metadata);
        Check(HostDiscoveryCodec.Decode(HostDiscoveryCodec.Encode(metadata with { ProtocolMajor = 99 }))!.ProtocolMajor == 99);
        Check(HostDiscoveryCodec.Decode([]) is null && HostDiscoveryCodec.Decode(new byte[513]) is null && HostDiscoveryCodec.Decode([0xff]) is null);
        Check(HostDiscoveryCodec.Decode(packet.AsSpan(0, packet.Length - 1)) is null);
        foreach (var change in new Action<HostDiscoveryAdvertisement>[] {
            value => value.Marker = "legacy-lan", value => value.ClaimedHostId = Guid.Empty.ToString("D"),
            value => value.ClaimedHostId = "not-a-host", value => value.Protocol = null, value => value.Protocol.Major = 0,
            value => value.PeerPort = 0, value => value.PairingPort = 65536 })
        {
            var value = HostDiscoveryAdvertisement.Parser.ParseFrom(packet); change(value);
            Check(HostDiscoveryCodec.Decode(value.ToByteArray()) is null);
        }
        // Unknown optional fields remain advisory metadata; no feature/authority is negotiated here.
        using var stream = new MemoryStream(); stream.Write(packet);
        using (var writer = new CodedOutputStream(stream, true)) { writer.WriteTag(100, WireFormat.WireType.Varint); writer.WriteUInt32(123); writer.Flush(); }
        Check(HostDiscoveryCodec.Decode(stream.ToArray()) == metadata);
        Invalid(() => HostDiscoveryCodec.Encode(metadata with { ClaimedHostId = Guid.Empty }));
        return Task.CompletedTask;
    }
    public static Task DirectorySourceBoundsAndExpiry()
    {
        var clock = new Clock(); var self = Guid.NewGuid(); var directory = new HostDiscoveryDirectory(self, clock);
        var ad = new UnverifiedHostAdvertisement(Guid.NewGuid(), 1, 7, 5000, 5001); var bytes = HostDiscoveryCodec.Encode(ad);
        Check(directory.Observe(IPAddress.Parse("192.0.2.1"), bytes));
        Check(directory.Observe(IPAddress.Parse("::ffff:192.0.2.1"), bytes) && directory.Snapshot().Count == 1);
        Check(directory.Observe(IPAddress.Parse("192.0.2.2"), bytes) && directory.Snapshot().Count == 2);
        Check(directory.Observe(IPAddress.Parse("192.0.2.1"), HostDiscoveryCodec.Encode(ad with { PairingPort = 6001 })));
        var old = directory.Snapshot(); Check(old.Count == 3 && old.All(value => value.Advertisement.ClaimedHostId == ad.ClaimedHostId));
        Check(old.Any(value => value.PairingAddress.HttpsAddress == new Uri("https://192.0.2.2:5001/")));
        Check(!directory.Observe(IPAddress.Any, bytes) && !directory.Observe(IPAddress.Parse("ff02::1"), bytes));
        Check(!directory.Observe(IPAddress.Loopback, HostDiscoveryCodec.Encode(ad with { ClaimedHostId = self })));
        for (var i = 3; i < HostDiscoveryDirectory.MaximumEntries; i++)
            Check(directory.Observe(IPAddress.Parse("192.0.2.1"), HostDiscoveryCodec.Encode(ad with { PeerPort = 10000 + i })));
        Check(directory.Snapshot().Count == HostDiscoveryDirectory.MaximumEntries);
        Check(!directory.Observe(IPAddress.Parse("192.0.2.3"), bytes));
        clock.Utc = clock.Utc.AddYears(-1); clock.Stamp = 11000;
        Check(directory.Observe(IPAddress.Parse("192.0.2.2"), bytes));
        clock.Stamp = 12000; Check(directory.Snapshot().Count == 1 && old.Count == 3);
        Check(directory.Observe(IPAddress.Parse("192.0.2.3"), bytes));
        clock.Stamp = 24000; Check(directory.Snapshot().Count == 0);
        Check(new HostDiscoveryDirectory(self).Snapshot().Count == 0);
        Parallel.For(0, 64, _ => Check(directory.Observe(IPAddress.Parse("192.0.2.1"), bytes)));
        Check(directory.Snapshot().Count == 1);
        return Task.CompletedTask;
    }
}
