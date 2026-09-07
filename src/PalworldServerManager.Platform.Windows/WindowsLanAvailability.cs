using System.Net.NetworkInformation;
using System.Net.Sockets;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Platform.Windows;

// Call only at actual Windows IO/inventory origins, never around a Host callback or cleanup.
internal static class WindowsLanAvailability
{
    internal static bool IsTemporary(Exception error) => error switch
    {
        SocketException socket => socket.SocketErrorCode is SocketError.NetworkDown or SocketError.NetworkUnreachable or
            SocketError.NetworkReset or SocketError.HostDown or SocketError.HostUnreachable or SocketError.ConnectionAborted or
            SocketError.ConnectionReset or SocketError.TimedOut or SocketError.AddressNotAvailable or
            SocketError.NoBufferSpaceAvailable or SocketError.SystemNotReady,
        NetworkInformationException inventory => inventory.NativeErrorCode is 232 or 1228, // SDK ERROR_NO_DATA / ERROR_ADDRESS_NOT_ASSOCIATED.
        _ => false
    };
    internal static T Invoke<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception ex) when (IsTemporary(ex)) { throw new LanDiscoveryUnavailableException(ex); }
    }
    internal static async ValueTask<T> InvokeAsync<T>(Func<ValueTask<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception ex) when (IsTemporary(ex)) { throw new LanDiscoveryUnavailableException(ex); }
    }
    internal static Socket CreateSocket(Func<Socket> create, Action<Socket> setup)
    {
        Socket? socket = null;
        try { socket = create(); setup(socket); return socket; }
        catch (Exception ex)
        {
            try { socket?.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(ex, cleanup); }
            if (IsTemporary(ex)) throw new LanDiscoveryUnavailableException(ex);
            throw;
        }
    }
    internal static IReadOnlyList<LanDiscoveryLink> ReadLinks() => Invoke(() => new WindowsLanInterfaceSource().Read());
}
