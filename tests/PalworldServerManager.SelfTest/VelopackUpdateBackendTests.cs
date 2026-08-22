using PalworldServerManager.Core.Models;
using PalworldServerManager.Core.Services.Update;
using Velopack;

namespace PalworldServerManager.SelfTest;

/// <summary>
/// Regression coverage for the Codex PR #3 P2 finding: DownloadUpdatesAsync must use the same
/// channel that produced the checked UpdateInfo, not a hardcoded default - otherwise a
/// Prerelease check followed by a hardcoded-Stable download risks resolving/fetching against the
/// wrong channel's release feed, directly threatening the planned v0.4.0-alpha.1 -> alpha.2 field
/// test. These tests exercise real (non-mocked) Velopack UpdateInfo/VelopackAsset objects against
/// VelopackUpdateBackend.ResolveDownloadChannel, the exact same static logic DownloadUpdatesAsync
/// itself calls to pick its channel - only the network download call is out of reach without
/// hitting GitHub, and constructing a full VelopackUpdateBackend instance requires a live
/// Velopack-initialized process (VelopackApp.Build().Run()) that the self-test harness never has.
/// </summary>
internal static class VelopackUpdateBackendTests
{
    public static Task TestDownloadUsesTheChannelThatProducedTheUpdateNotAHardcodedDefault()
    {
        var stableRelease = MakeRelease("1.0.0", UpdateChannel.Stable);
        Equal(UpdateChannel.Stable, VelopackUpdateBackend.ResolveDownloadChannel(stableRelease));

        var prereleaseRelease = MakeRelease("1.0.0-beta.1", UpdateChannel.Prerelease);
        // This is the exact old regression: DownloadUpdatesAsync used to build its manager with a
        // hardcoded UpdateChannel.Stable regardless of which channel actually produced the
        // release, which this assertion would have caught immediately.
        Equal(UpdateChannel.Prerelease, VelopackUpdateBackend.ResolveDownloadChannel(prereleaseRelease));
        return Task.CompletedTask;
    }

    public static Task TestDownloadRejectsAReleaseNotProducedByThisBackend()
    {
        var foreignRelease = new ReleaseInfo { Version = "1.0.0", BackendToken = "not a real backend token" };

        Exception? caught = null;
        try { VelopackUpdateBackend.ResolveDownloadChannel(foreignRelease); }
        catch (InvalidOperationException ex) { caught = ex; }
        True(caught is not null, "a release not produced by this backend's own CheckForUpdatesAsync must be rejected, not silently defaulted");
        return Task.CompletedTask;
    }

    private static ReleaseInfo MakeRelease(string version, UpdateChannel channel)
    {
        var asset = new VelopackAsset
        {
            PackageId = "ShoeMin.PalworldServerManager",
            Version = SemanticVersion.Parse(version),
            FileName = $"ShoeMin.PalworldServerManager-{version}-full.nupkg",
            Size = 1
        };
        var info = new UpdateInfo(asset, isDowngrade: false);
        return new ReleaseInfo
        {
            Version = version,
            BackendToken = new VelopackUpdateBackend.ResolvedUpdate(info, channel)
        };
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected '{expected}', got '{actual}'.");
    }
}
