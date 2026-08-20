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

    Task<UpdateCheckResult> CheckForUpdatesAsync(UpdateChannel channel, CancellationToken cancellationToken);

    /// <summary>Downloads/stages the given release. Must not apply it or restart the application.</summary>
    Task DownloadUpdatesAsync(ReleaseInfo release, IProgress<int> progress, CancellationToken cancellationToken);
}
