using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services.Update;

/// <summary>
/// Abstracts the actual update mechanism (Velopack in production) away from
/// ApplicationUpdateService so the state machine, channel handling, and concurrency logic can
/// be unit-tested with a fake backend that never touches GitHub or a real installed copy.
/// </summary>
public interface IApplicationUpdateBackend
{
    UpdateExecutionMode ExecutionMode { get; }
    string CurrentVersion { get; }

    /// <summary>
    /// The channel this specific installed/portable package was actually built for, if known -
    /// e.g. a package built with `vpk pack --channel win-beta` reports Prerelease here. Null
    /// when not determinable (Development mode, or the package predates channel tagging). Used
    /// only to pick a sensible *initial* channel preference the first time the Manager ever
    /// runs; an explicitly saved user preference always takes priority afterward.
    /// </summary>
    UpdateChannel? InstalledChannel { get; }

    Task<UpdateCheckResult> CheckForUpdatesAsync(UpdateChannel channel, CancellationToken cancellationToken);

    /// <summary>Downloads/stages the given release. Must not apply it or restart the application.</summary>
    Task DownloadUpdatesAsync(ReleaseInfo release, IProgress<int> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Launches the external updater, which will wait briefly for this process to exit on its
    /// own before applying the staged release and restarting. Returns once the updater has been
    /// launched - it does NOT itself exit this process. The caller must finish its own graceful
    /// shutdown and then exit immediately afterward.
    /// </summary>
    void BeginApplyAndRestart(ReleaseInfo release);
}
