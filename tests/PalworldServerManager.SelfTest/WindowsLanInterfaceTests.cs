using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using PalworldServerManager.Platform.Contracts;
using Source = PalworldServerManager.Platform.Windows.WindowsLanInterfaceSource;

namespace PalworldServerManager.SelfTest;

internal static class WindowsLanInterfaceTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Windows LAN interface assertion failed."); }
    private static void Invalid(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid LAN link accepted."); }
    public static Task LinkSourcePolicy()
    {
        var id = Guid.NewGuid(); var link = new LanDiscoveryLink(42, id, 7, IPAddress.Parse("192.0.2.5"), 24);
        Check(link.LocalAddress.ToString() == "192.0.2.5" && link.BroadcastAddress.ToString() == "192.0.2.255");
        Check(link.AdmitsSource(7, IPAddress.Parse("192.0.2.6")));
        Check(!link.AdmitsSource(8, IPAddress.Parse("192.0.2.6")));
        foreach (var address in new[] { "192.0.2.5", "192.0.2.0", "192.0.2.255", "192.0.3.6", "0.0.0.0", "127.0.0.1", "224.0.0.1", "::ffff:192.0.2.6", "::1" })
            Check(!link.AdmitsSource(7, IPAddress.Parse(address)));
        var bytes = link.LocalAddress.GetAddressBytes(); bytes[0] = 203; Check(link.LocalAddress.ToString() == "192.0.2.5");
        var small = new LanDiscoveryLink(42, id, 7, IPAddress.Parse("198.51.100.1"), 30);
        Check(small.BroadcastAddress.ToString() == "198.51.100.3" && small.AdmitsSource(7, IPAddress.Parse("198.51.100.2")));
        foreach (var prefix in new[] { -1, 0, 31, 32, 33 }) Invalid(() => new LanDiscoveryLink(42, id, 7, IPAddress.Parse("192.0.2.5"), prefix));
        foreach (var address in new[] { "192.0.2.0", "192.0.2.255", "0.0.0.1", "127.0.0.1", "224.0.0.1", "255.255.255.255", "2001:db8::1" })
            Invalid(() => new LanDiscoveryLink(42, id, 7, IPAddress.Parse(address), 24));
        Invalid(() => new LanDiscoveryLink(0, id, 7, IPAddress.Parse("192.0.2.5"), 24));
        Invalid(() => new LanDiscoveryLink(42, Guid.Empty, 7, IPAddress.Parse("192.0.2.5"), 24));
        Invalid(() => new LanDiscoveryLink(42, id, 0, IPAddress.Parse("192.0.2.5"), 24));
        return Task.CompletedTask;
    }
    public static Task HardwareEligibilityAndCorrelation()
    {
        var id = Guid.NewGuid(); var input = new Source.AdapterAddress(id, 7, IPAddress.Parse("192.0.2.5"), 24);
        var valid = new Source.InterfaceState(42, 7, id, 6, 0, 0, 14, 2, 0, 5, 1, 1, 1, 1);
        Check(Source.Select([input], _ => valid).Count == 1);
        Check(Source.Select([input], _ => valid with { Type = 71, PhysicalMediumType = 9, MediaType = 16 }).Count == 1);
        for (var flags = 0; flags <= 255; flags++) if (flags != 5)
            Check(Source.Select([input], _ => valid with { Flags = (byte)flags }).Count == 0);
        foreach (var state in new[] { valid with { Luid = 0 }, valid with { Id = Guid.NewGuid() }, valid with { Index = 8 },
            valid with { Type = 23 }, valid with { Type = 131 }, valid with { Type = 243 }, valid with { Type = uint.MaxValue },
            valid with { TunnelType = 1 }, valid with { PhysicalMediumType = 0 }, valid with { PhysicalMediumType = 8 },
            valid with { MediaType = 16 }, valid with { AccessType = 1 }, valid with { DirectionType = 1 },
            valid with { OperStatus = 2 }, valid with { AdminStatus = 2 }, valid with { MediaState = 0 }, valid with { ConnectionType = 3 } })
            Check(Source.Select([input], _ => state).Count == 0);
        Check(Source.Select([input], _ => null).Count == 0);
        Check(Source.Select([input with { Id = Guid.NewGuid() }], _ => valid).Count == 0);
        Check(Source.Select([input with { PrefixLength = 0 }], _ => valid).Count == 0);
        Check(Source.Select([input with { Address = IPAddress.Loopback }], _ => valid).Count == 0);
        Check(Source.Select([input, input], _ => valid).Count == 1);
        // A reused index with another GUID never inherits the first adapter's classification.
        Check(Source.Select([input, input with { Id = Guid.NewGuid() }], _ => valid).Count == 1);
        var queries = 0;
        var addresses = Enumerable.Range(1, 400).Select(i => input with { Address = new IPAddress(new byte[] { 10, 1, (byte)(i >> 8), (byte)i }), PrefixLength = 16 });
        Check(Source.Select(addresses, _ => { queries++; return valid; }).Count == Source.MaximumAddresses && queries == 1);
        return Task.CompletedTask;
    }
    public static Task NativeLayoutAndReadOnlyInventory()
    {
        Check(Marshal.SizeOf<Source.NativeRow>() == 1352);
        foreach (var pair in new[] { (nameof(Source.NativeRow.InterfaceIndex), 8), (nameof(Source.NativeRow.InterfaceGuid), 12),
            (nameof(Source.NativeRow.Type), 1128), (nameof(Source.NativeRow.Flags), 1152), (nameof(Source.NativeRow.OperStatus), 1156),
            (nameof(Source.NativeRow.NetworkGuid), 1168), (nameof(Source.NativeRow.ConnectionType), 1184),
            (nameof(Source.NativeRow.TransmitLinkSpeed), 1192), (nameof(Source.NativeRow.OutQLen), 1344) })
            Check(Marshal.OffsetOf<Source.NativeRow>(pair.Item1).ToInt32() == pair.Item2);
        Check(Source.ReadNative(0) is null);
        var index = NetworkInterface.LoopbackInterfaceIndex; var loopback = Source.ReadNative((uint)index);
        Check(loopback is not null && loopback.Index == index && loopback.Luid != 0 && loopback.Id != Guid.Empty && loopback.Type == 24);
        Check(NetworkInterface.GetAllNetworkInterfaces().Any(adapter => Guid.TryParse(adapter.Id, out var id) && id == loopback!.Id));
        var links = new Source().Read(); Check(links.Count <= Source.MaximumAddresses);
        foreach (var link in links)
        {
            Check(link.InterfaceIndex != index && link.InterfaceLuid != 0 && link.InterfaceId != Guid.Empty);
            Check(!link.AdmitsSource(link.InterfaceIndex, link.LocalAddress) && !link.AdmitsSource(link.InterfaceIndex, link.BroadcastAddress));
        }
        // Zero eligible physical links in a VM/CI environment is legitimate, not LAN field proof.
        return Task.CompletedTask;
    }
}
