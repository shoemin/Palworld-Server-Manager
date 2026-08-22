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
            // Retains which channel actually produced this UpdateInfo, not just the UpdateInfo
            // itself - see DownloadUpdatesAsync for why this matters.
            BackendToken = new ResolvedUpdate(info, channel)
        };
        _logger.Info($"Update check against channel '{ChannelName(channel)}' found version {release.Version}.");
        return new UpdateCheckResult(true, release);
    }

    public async Task DownloadUpdatesAsync(ReleaseInfo release, IProgress<int> progress, CancellationToken cancellationToken)
    {
        var resolved = RequireResolvedUpdate(release);
        // Must use the SAME channel that produced this UpdateInfo, not whatever channel happens
        // to be currently selected: a Prerelease check followed by a hardcoded-Stable download
        // manager risks resolving/fetching against the wrong channel's release feed.
        var manager = CreateManager(resolved.Channel);
        await manager.DownloadUpdatesAsync(resolved.Info, percent => progress.Report(percent), cancellationToken);
    }

    /// <summary>
    /// The channel DownloadUpdatesAsync will actually use to build its Velopack UpdateManager for
    /// this release - i.e. the channel that produced it via CheckForUpdatesAsync, not whatever
    /// channel is currently selected. Static and instance-independent (it only inspects the
    /// release's own token) so it can be verified directly in a self-test without constructing a
    /// real VelopackUpdateBackend, which requires a live Velopack-initialized process and cannot
    /// run standalone in the self-test harness.
    /// </summary>
    public static UpdateChannel ResolveDownloadChannel(ReleaseInfo release) => RequireResolvedUpdate(release).Channel;

    public void BeginApplyAndRestart(ReleaseInfo release)
    {
        var resolved = RequireResolvedUpdate(release);
        // Deliberately still Stable/win here, not resolved.Channel: confirmed against the pinned
        // Velopack 1.2.0 source (src/lib-csharp/UpdateManager.cs) that WaitExitThenApplyUpdates ->
        // UpdateExe.Apply(Locator, toApply, ...) never reads Source or Channel at all - only
        // CheckForUpdatesInternal ever consults UpdateOptions.ExplicitChannel (to pick which
        // release feed to check), and DownloadReleaseEntry resolves its asset URL from the
        // already-known GitBaseAsset.Release captured at check time, not from this manager
        // instance's own channel/source. Applying an already-downloaded, already-verified local
        // package is therefore genuinely channel-independent, unlike downloading (above).
        var manager = CreateManager(UpdateChannel.Stable);
        _logger.Info($"Launching the external Velopack updater for version {release.Version}. Palworld Server Manager will exit shortly; Palworld is not affected by this call.");
        // WaitExitThenApplyUpdates (rather than the one-shot ApplyUpdatesAndRestart) launches the
        // external updater and returns immediately, leaving this process's own shutdown sequence
        // (stopping LAN/Dashboard, then exiting) fully under our control instead of Velopack's.
        manager.WaitExitThenApplyUpdates(resolved.Info.TargetFullRelease, silent: false, restart: true, restartArgs: []);
    }

    private static ResolvedUpdate RequireResolvedUpdate(ReleaseInfo release)
    {
        if (release.BackendToken is not ResolvedUpdate resolved)
            throw new InvalidOperationException("Release was not produced by a check against this backend.");
        return resolved;
    }

    /// <summary>Pairs a Velopack UpdateInfo with the channel that actually produced it, so later stages (download, in particular) use that same channel rather than an unrelated default.</summary>
    public sealed record ResolvedUpdate(UpdateInfo Info, UpdateChannel Channel);

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
