namespace PalworldServerManager.Platform.Contracts;

public enum FileReadFailure { InvalidPath, NotFound, AccessDenied, Busy, UnsafeObject, TooLarge, Changed, Unavailable }

public sealed class BoundedFileReadException(FileReadFailure failure) : IOException("Bounded file read refused.")
{
    public FileReadFailure Failure { get; } = failure;
}

// Trusted Host input only. This seam does not authorize the root, prove stopped state,
// assign a configuration revision, or exclude pre-existing writable memory mappings.
public interface IBoundedFileReader
{
    byte[] Read(string root, IReadOnlyList<string> relativeComponents, int maximumBytes, CancellationToken ct = default);
}
