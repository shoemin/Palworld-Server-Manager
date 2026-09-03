namespace PalworldServerManager.Host.Persistence;

// The machine-wide root directory Host state lives under. Host.Persistence owns the LAYOUT
// beneath this root (database filename, snapshot locations); it deliberately does NOT discover
// the root itself - that is OS-specific (ProgramData on Windows) and belongs at the Host /
// Host.Cli composition root or the platform seam (#41), never in shared persistence code
// (PLATFORM-001). Tests inject a temporary directory, so no test ever touches a developer's
// real machine-wide Manager data root.
public sealed class HostDataRoot
{
    public HostDataRoot(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("A machine-wide Host data root must be supplied by the caller.", nameof(rootDirectory));
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    // Relative layout owned by Host.Persistence.
    public string DatabasePath => Path.Combine(RootDirectory, "host.db");

    public string SnapshotsDirectory => Path.Combine(RootDirectory, "snapshots");

    public void EnsureCreated() => Directory.CreateDirectory(RootDirectory);
}
