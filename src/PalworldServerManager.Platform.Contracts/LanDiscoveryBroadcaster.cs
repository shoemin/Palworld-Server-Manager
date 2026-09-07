namespace PalworldServerManager.Platform.Contracts;

public interface ILanDiscoveryBroadcaster
{
    // One owned round. The count means local socket acceptance, never delivery or trust.
    ValueTask<int> BroadcastAsync(ReadOnlyMemory<byte> publicPacket, CancellationToken cancellationToken);
}
