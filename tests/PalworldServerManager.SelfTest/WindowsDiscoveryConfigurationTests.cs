using System.Net;
using System.Net.Sockets;
using PalworldServerManager.Contracts;
using PalworldServerManager.Host;

namespace PalworldServerManager.SelfTest;

internal static class WindowsDiscoveryConfigurationTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Windows discovery configuration assertion failed."); }
    private static void Invalid(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid discovery configuration accepted."); }
    private static IPEndPoint Any() => new(IPAddress.Any, 0);
    public static Task ExplicitPortAndCompatibleBindings()
    {
        Check(WindowsHostComposition.CreateWindowsDiscoveryFactory(null, new(IPAddress.Loopback, 1234), new(IPAddress.IPv6Loopback, 2345)) is null);
        foreach (var port in new[] { int.MinValue, -1, 0, 65536, int.MaxValue })
            Invalid(() => WindowsHostComposition.CreateWindowsDiscoveryFactory(port, Any(), Any()));
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback, IPAddress.IPv6Any, IPAddress.Broadcast,
            IPAddress.Parse("::ffff:0.0.0.0"), IPAddress.Parse("192.0.2.7"), IPAddress.Parse("2001:db8::7") })
        {
            Invalid(() => WindowsHostComposition.CreateWindowsDiscoveryFactory(45678, new(address, 5000), Any()));
            Invalid(() => WindowsHostComposition.CreateWindowsDiscoveryFactory(45678, Any(), new(address, 5001)));
        }
        foreach (var port in new[] { 1, 45678, 65535 })
            Check(WindowsHostComposition.CreateWindowsDiscoveryFactory(port, Any(), Any()) is not null); // Construction opens nothing.
        Invalid(() => WindowsHostComposition.CreateWindowsDiscoveryFactory(45678, null!, Any()));
        Invalid(() => WindowsHostComposition.CreateWindowsDiscoveryFactory(45678, Any(), null!));
        return Task.CompletedTask;
    }
    private static async Task<Exception> Refused(Func<UnverifiedHostAdvertisement, CancellationToken, Task<HostDiscoveryRuntime>> factory,
        UnverifiedHostAdvertisement ad, CancellationToken token)
    {
        HostDiscoveryRuntime? returned = null;
        try { returned = await factory(ad, token).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception ex) when (ex is not TimeoutException) { return ex; }
        finally { if (returned is not null) await returned.DisposeAsync(); }
        throw new Exception("Concrete discovery factory unexpectedly started.");
    }
    public static async Task ActualPortCollisionAndPreCancellation()
    {
        int port;
        using (var guard = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true })
        {
            guard.Bind(new IPEndPoint(IPAddress.Any, 0)); port = ((IPEndPoint)guard.LocalEndPoint!).Port;
            var factory = WindowsHostComposition.CreateWindowsDiscoveryFactory(port, Any(), Any())!;
            var ad = new UnverifiedHostAdvertisement(Guid.NewGuid(), 1, 7, 5000, 5001);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Check(await Refused(factory, ad, canceled.Token) is OperationCanceledException);
            // The actual exclusive bind fails before HostDiscoveryRuntime launches any sender.
            // Both Windows exclusive-bind error forms are deliberately outside availability retry.
            var failure = await Refused(factory, ad, CancellationToken.None);
            Check(failure is SocketException socket && socket.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied);
            Check(((IPEndPoint)guard.LocalEndPoint!).Port == port);
        }
        using var rebind = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        rebind.Bind(new IPEndPoint(IPAddress.Any, port));
    }
}
