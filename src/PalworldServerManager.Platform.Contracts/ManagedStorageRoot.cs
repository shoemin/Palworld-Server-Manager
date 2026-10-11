namespace PalworldServerManager.Platform.Contracts;

// Host composition only; this is neither a server identity nor a filesystem permit.
public interface IManagedStorageRoot
{
    string Path { get; }
    void Validate(CancellationToken ct = default);
}
