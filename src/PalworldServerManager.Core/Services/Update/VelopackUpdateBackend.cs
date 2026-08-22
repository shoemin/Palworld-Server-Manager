using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;
using Velopack;
using Velopack.Locators;
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
        InstalledChannel = ReadInstalledChannel();
        _logger.Info($"Update backend initialized. ExecutionMode={ExecutionMode} CurrentVersion={CurrentVersion} InstalledChannel={(InstalledChannel is { } c ? ChannelName(c) : "unknown")}.");
    }

    public UpdateExecutionMode ExecutionMode { get; }
    public string CurrentVersion { get; }
    public UpdateChannel? InstalledChannel { get; }

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

    public void BeginApplyAndRestart(ReleaseInfo release)
    {
        if (release.BackendToken is not UpdateInfo info)
            throw new InvalidOperationException("Release was not produced by a check against this backend.");

        var manager = CreateManager(UpdateChannel.Stable); // channel is irrelevant to applying an already-resolved asset
        _logger.Info($"Launching the external Velopack updater for version {release.Version}. Palworld Server Manager will exit shortly; Palworld is not affected by this call.");
        // WaitExitThenApplyUpdates (rather than the one-shot ApplyUpdatesAndRestart) launches the
        // external updater and returns immediately, leaving this process's own shutdown sequence
        // (stopping LAN/Dashboard, then exiting) fully under our control instead of Velopack's.
        manager.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: false, restart: true, restartArgs: []);
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

    /// <summary>
    /// Reads the channel this specific package was actually built for from Velopack's own
    /// ambient locator (set process-wide by VelopackApp.Build().Run() at startup, before this
    /// class is ever constructed) - not from anything this Manager decided. Returns null when
    /// there's no locator to ask (Development mode) or its channel name isn't one of ours.
    /// </summary>
    private UpdateChannel? ReadInstalledChannel()
    {
        try
        {
            if (!VelopackLocator.IsCurrentSet) return null;
            var channel = VelopackLocator.Current.Channel;
            if (string.Equals(channel, PrereleaseChannel, StringComparison.OrdinalIgnoreCase)) return UpdateChannel.Prerelease;
            if (string.Equals(channel, StableChannel, StringComparison.OrdinalIgnoreCase)) return UpdateChannel.Stable;
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not read the installed package's Velopack channel: {ex.Message}");
            return null;
        }
    }

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
