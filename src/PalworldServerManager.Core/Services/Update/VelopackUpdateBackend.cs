using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;
using Velopack;
using Velopack.Sources;

namespace PalworldServerManager.Core.Services.Update;

/// <summary>
/// Real IApplicationUpdateBackend backed by Velopack against this project's public GitHub
/// Releases. Anonymous only - no access token, no scraping. The default packaged channel is
/// "win" (Velopack's OS-name default, matching the current unqualified `vpk pack`); prerelease
/// packaging under "win-beta" is a 4F/release-pipeline concern this backend anticipates but
/// does not itself produce.
/// </summary>
public sealed class VelopackUpdateBackend : IApplicationUpdateBackend
{
    private const string RepoUrl = "https://github.com/shoemin/Palworld-Server-Manager";
    private const string StableChannel = "win";
    private const string PrereleaseChannel = "win-beta";

    private readonly IAppLogger _logger;

    public VelopackUpdateBackend(IAppLogger logger)
    {
        _logger = logger;

        // Channel doesn't affect install-mode/current-version reads, so a Stable-configured
        // manager is enough to answer those regardless of which channel the user has selected.
        var probe = CreateManager(UpdateChannel.Stable);
        ExecutionMode = UpdateExecutionModeDetector.Detect(probe.IsInstalled, probe.IsPortable, AppContext.BaseDirectory);
        CurrentVersion = ReadCurrentVersion(probe);
        _logger.Info($"Update backend initialized. ExecutionMode={ExecutionMode} CurrentVersion={CurrentVersion}.");
    }

    public UpdateExecutionMode ExecutionMode { get; }
    public string CurrentVersion { get; }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(UpdateChannel channel, CancellationToken cancellationToken)
    {
        var manager = CreateManager(channel);
        // Velopack 1.2.0's CheckForUpdatesAsync has no cancellation overload; it is a single
        // short HTTP request against the release feed and cannot be preempted mid-flight here.
        cancellationToken.ThrowIfCancellationRequested();
        var info = await manager.CheckForUpdatesAsync();
        cancellationToken.ThrowIfCancellationRequested();

        if (info is null)
        {
            _logger.Info($"Update check against channel '{ChannelName(channel)}' found no newer release.");
            return new UpdateCheckResult(false, null);
        }

        var asset = info.TargetFullRelease;
        var release = new ReleaseInfo
        {
            Version = asset.Version.ToNormalizedString(),
            ReleaseNotes = string.IsNullOrWhiteSpace(asset.NotesMarkdown) ? null : asset.NotesMarkdown,
            SizeBytes = asset.Size > 0 ? asset.Size : null,
            // Velopack's asset metadata does not carry a release-date field; left unavailable
            // rather than fabricated.
            ReleaseDate = null,
            BackendToken = info
        };
        _logger.Info($"Update check against channel '{ChannelName(channel)}' found version {release.Version}.");
        return new UpdateCheckResult(true, release);
    }

    public async Task DownloadUpdatesAsync(ReleaseInfo release, IProgress<int> progress, CancellationToken cancellationToken)
    {
        if (release.BackendToken is not UpdateInfo info)
            throw new InvalidOperationException("Release was not produced by a check against this backend.");

        var manager = CreateManager(UpdateChannel.Stable); // channel is irrelevant to downloading an already-resolved UpdateInfo
        await manager.DownloadUpdatesAsync(info, percent => progress.Report(percent), cancellationToken);
    }

    private UpdateManager CreateManager(UpdateChannel channel)
    {
        var source = new GithubSource(RepoUrl, accessToken: null, prerelease: channel == UpdateChannel.Prerelease);
        var options = new UpdateOptions
        {
            ExplicitChannel = ChannelName(channel),
            AllowVersionDowngrade = false
        };
        return new UpdateManager(source, options, locator: null!);
    }

    private static string ChannelName(UpdateChannel channel) => channel == UpdateChannel.Prerelease ? PrereleaseChannel : StableChannel;

    private string ReadCurrentVersion(UpdateManager manager)
    {
        try
        {
            return manager.CurrentVersion?.ToNormalizedString()
                ?? typeof(VelopackUpdateBackend).Assembly.GetName().Version?.ToString()
                ?? "unknown";
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not read current version from Velopack; falling back to assembly version: {ex.Message}");
            return typeof(VelopackUpdateBackend).Assembly.GetName().Version?.ToString() ?? "unknown";
        }
    }
}
