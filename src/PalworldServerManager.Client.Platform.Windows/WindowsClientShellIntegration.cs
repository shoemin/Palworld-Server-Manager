using System.Diagnostics;
using PalworldServerManager.Client.Platform.Contracts;

namespace PalworldServerManager.Client.Platform.Windows;

/// <summary>
/// Windows client-side shell integration (SS11a, PLATFORM-002).
///
/// Opens an already-validated LOCAL directory only. It never builds a command string from caller
/// input, never resolves a remote ServerRef, and never opens a remote path - the launcher receives
/// a directory path and nothing else, so there is no argument-injection surface.
/// </summary>
public sealed class WindowsClientShellIntegration : IClientShellIntegration
{
    private readonly IShellProcessLauncher _launcher;

    public WindowsClientShellIntegration(IShellProcessLauncher? launcher = null)
        => _launcher = launcher ?? new ExplorerShellProcessLauncher();

    public Task OpenLocalFolderAsync(string localDirectoryPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDirectoryPath);
        ct.ThrowIfCancellationRequested();

        var full = Path.GetFullPath(localDirectoryPath);

        // Reject anything that is not a plain local directory. A UNC/remote path is never
        // "locally openable" merely because it was handed to a client.
        if (!Path.IsPathFullyQualified(full) || full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only a fully qualified local directory path may be opened.", nameof(localDirectoryPath));
        }

        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Local directory does not exist: {full}");
        }

        _launcher.LaunchFolder(full);
        return Task.CompletedTask;
    }
}

internal sealed class ExplorerShellProcessLauncher : IShellProcessLauncher
{
    public void LaunchFolder(string localDirectoryPath)
    {
        // UseShellExecute with the directory as the target - no command string is composed.
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = localDirectoryPath,
            UseShellExecute = true,
        });
    }
}
