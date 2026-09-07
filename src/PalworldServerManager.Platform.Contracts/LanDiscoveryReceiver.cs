using System.Net;

namespace PalworldServerManager.Platform.Contracts;

// Unverified public hints only. The payload has independent storage for each callback.
public delegate ValueTask LanDiscoveryReceived(int actualInterfaceIndex, IPAddress actualSource,
    ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

public interface ILanDiscoveryReceiver : IAsyncDisposable
{
    int Port { get; }
    // Completes only after socket and callback cleanup; faults must be observed by the Host owner.
    Task Completion { get; }
}
