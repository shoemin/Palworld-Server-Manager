using System.Security.Cryptography;

namespace PalworldServerManager.Platform.Contracts;

// Trusted Host-only input, after exact Host/server registration and current read authorization.
// Neither this seam nor a recorded path grants access. No filenames come from the request.
public interface IManagedServerSettingsReader
{
    ManagedServerSettingsFiles Read(Guid profile, string? registeredInstallPath, CancellationToken ct = default);
}

// Owns raw (potentially secret) buffers, never a wire DTO or loggable value record.
// Each file is an independent observation; the pair is not an atomic filesystem snapshot.
public sealed class ManagedServerSettingsFiles : IDisposable
{
    public const int MaximumFileBytes = 4_194_308;
    private byte[]? active;
    private byte[]? defaults;
    // Transfers ownership; callers must not retain or mutate the supplied arrays.
    public ManagedServerSettingsFiles(byte[] active, byte[]? defaults)
    {
        ArgumentNullException.ThrowIfNull(active);
        if (active.Length > MaximumFileBytes || defaults?.Length > MaximumFileBytes)
            throw new ArgumentOutOfRangeException(nameof(active));
        this.active = active; this.defaults = defaults;
    }
    public ReadOnlyMemory<byte> Active => active ?? throw new ObjectDisposedException(nameof(ManagedServerSettingsFiles));
    public ReadOnlyMemory<byte>? InstallationDefaults
    {
        get
        {
            ObjectDisposedException.ThrowIf(active is null, this);
            return defaults is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(defaults);
        }
    }
    // Single-owner lifetime, not concurrent access/disposal. Retained views are zeroed too.
    public void Dispose()
    {
        if (active is not null) CryptographicOperations.ZeroMemory(active);
        if (defaults is not null) CryptographicOperations.ZeroMemory(defaults);
        active = null; defaults = null;
    }
}
