using System.Security.Cryptography;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Platform.Windows;

public sealed class WindowsManagedServerSettingsReader : IManagedServerSettingsReader
{
    private readonly IManagedStorageRoot root;
    private readonly IBoundedFileReader reader;
    public WindowsManagedServerSettingsReader(WindowsManagedStorageRoot root)
        : this(root, new WindowsBoundedFileReader()) { }
    internal WindowsManagedServerSettingsReader(IManagedStorageRoot root, IBoundedFileReader reader)
    { this.root = root ?? throw new ArgumentNullException(nameof(root)); this.reader = reader ?? throw new ArgumentNullException(nameof(reader)); }

    public ManagedServerSettingsFiles Read(Guid profile, string? registeredInstallPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var directory = profile.ToString("N");
        // An exact Host-generated spelling is required, including case. Never open the recorded path.
        var path = root.Path;
        if (profile == Guid.Empty || !string.Equals(registeredInstallPath, Path.Combine(path, directory), StringComparison.Ordinal))
            throw new BoundedFileReadException(FileReadFailure.InvalidPath);
        byte[]? active = null; byte[]? defaults = null;
        try
        {
            root.Validate(ct);
            active = reader.Read(path, [directory, "Pal", "Saved", "Config", "WindowsServer", "PalWorldSettings.ini"], ManagedServerSettingsFiles.MaximumFileBytes, ct);
            ct.ThrowIfCancellationRequested();
            try { defaults = reader.Read(path, [directory, "DefaultPalWorldSettings.ini"], ManagedServerSettingsFiles.MaximumFileBytes, ct); }
            catch (BoundedFileReadException ex) when (ex.Failure == FileReadFailure.NotFound) { }
            root.Validate(ct); ct.ThrowIfCancellationRequested();
            var result = new ManagedServerSettingsFiles(active, defaults);
            active = null; defaults = null; return result;
        }
        catch (BoundedFileReadException) { throw; }
        catch (UnauthorizedAccessException) { throw new BoundedFileReadException(FileReadFailure.AccessDenied); }
        catch (IOException) { throw new BoundedFileReadException(FileReadFailure.Unavailable); }
        finally
        {
            if (active is not null) CryptographicOperations.ZeroMemory(active);
            if (defaults is not null) CryptographicOperations.ZeroMemory(defaults);
        }
    }
}
