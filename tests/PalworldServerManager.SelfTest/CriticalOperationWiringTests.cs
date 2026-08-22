using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;
using PalworldServerManager.Core.Services;

namespace PalworldServerManager.SelfTest;

/// <summary>
/// Proves that each Manager-owned operation actually registers with a shared
/// ICriticalOperationTracker under the correct CriticalOperationKind - i.e. that the gating
/// wired into section 6/13's list is real, not just present on the tracker in isolation.
/// SteamCmd-dependent operations (server provisioning/update, legacy import, package import)
/// are wired the same way at their call sites but are not exercised here: they require a real
/// network/Steam dependency this self-test suite deliberately avoids. LAN transfer send/receive
/// wiring is verified in LanTests.cs, which already runs a real loopback transfer.
/// </summary>
internal static class CriticalOperationWiringTests
{
    public static async Task TestServerStartRegistersAsServerStart()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var tracker = new RecordingOperationTracker();
            var settings = new PalworldSettingsService(logger);
            var rest = new PalworldRestClient(logger);
            var processes = new ServerProcessService(settings, rest, logger, tracker);

            var profile = new ServerProfile { Name = "Start Wiring Test", InstallPath = Path.Combine(paths.ServersRoot, "start-test"), GamePort = 48211 };
            SyntheticPalServerHarness.CopyInto(profile.InstallPath); // a real, valid exe at PalServer.exe so the file-exists check passes and StartAsync really attempts a launch

            await processes.StartAsync(profile, [profile]);

            Equal(1, tracker.Calls.Count(c => c.Kind == CriticalOperationKind.ServerStart && c.Detail == profile.Name));
        });
    }

    public static async Task TestServerForceStopRegistersAsServerForceStop()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var tracker = new RecordingOperationTracker();
            var settings = new PalworldSettingsService(logger);
            var rest = new PalworldRestClient(logger);
            var processes = new ServerProcessService(settings, rest, logger, tracker);
            var profile = new ServerProfile { Name = "Force Stop Wiring Test", InstallPath = Path.Combine(paths.ServersRoot, "force-stop-test") };

            using var fake = SyntheticPalServerHarness.Start(profile.InstallPath, waitSeconds: 10, exitCode: 0);
            try
            {
                var result = await processes.StopAsync(profile, force: true);
                True(result.Success, "force-stopping a real synthetic process should succeed: " + result.Message);
                Equal(1, tracker.Calls.Count(c => c.Kind == CriticalOperationKind.ServerForceStop && c.Detail == profile.Name));
            }
            finally { SyntheticPalServerHarness.TryKill(fake); }
        });
    }

    public static async Task TestServerStopOnAnAlreadyStoppedServerRegistersNothing()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var tracker = new RecordingOperationTracker();
            var settings = new PalworldSettingsService(logger);
            var rest = new PalworldRestClient(logger);
            var processes = new ServerProcessService(settings, rest, logger, tracker);
            var profile = new ServerProfile { Name = "Stop Test", InstallPath = Path.Combine(paths.ServersRoot, "stop-test") };
            Directory.CreateDirectory(profile.InstallPath);

            await processes.StopAsync(profile, force: false);
            await processes.StopAsync(profile, force: true);
            True(tracker.Calls.Count == 0, "stopping an already-stopped server is a no-op and must not register a critical operation");
        });
    }

    public static async Task TestBackupRegistersAsBackup()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var tracker = new RecordingOperationTracker();
            var settings = new PalworldSettingsService(logger);
            var rest = new PalworldRestClient(logger);
            var processes = new ServerProcessService(settings, rest, logger);
            var backups = new BackupService(paths, processes, logger, tracker);

            var profile = new ServerProfile { Name = "Backup Test", InstallPath = Path.Combine(paths.ServersRoot, "backup-test") };
            Directory.CreateDirectory(profile.SavedPath);
            await File.WriteAllTextAsync(Path.Combine(profile.SavedPath, "marker.txt"), "x");

            await backups.CreateBackupAsync(profile);
            Equal(1, tracker.Calls.Count(c => c.Kind == CriticalOperationKind.Backup));
        });
    }

    public static async Task TestRestoreRegistersAsRestoreAndReleasesLeaseOnFailure()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var tracker = new RecordingOperationTracker();
            var settings = new PalworldSettingsService(logger);
            var rest = new PalworldRestClient(logger);
            var processes = new ServerProcessService(settings, rest, logger);
            var backups = new BackupService(paths, processes, logger, tracker);
            var profile = new ServerProfile { Name = "Restore Test", InstallPath = Path.Combine(paths.ServersRoot, "restore-test") };

            try { await backups.RestoreBackupAsync(profile, Path.Combine(paths.Root, "does-not-exist.zip")); }
            catch (FileNotFoundException) { }

            Equal(1, tracker.Calls.Count(c => c.Kind == CriticalOperationKind.Restore));
            True(!tracker.IsBusy, "the Restore lease must be released even though the backup file was missing and the call threw");
        });
    }

    public static async Task TestSettingsWriteRegistersAsSettingsWrite()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var tracker = new RecordingOperationTracker();
            var settings = new PalworldSettingsService(logger, tracker);
            var profile = new ServerProfile { Name = "Settings Test", InstallPath = Path.Combine(paths.ServersRoot, "settings-test") };

            await settings.SaveAsync(profile, []);
            Equal(1, tracker.Calls.Count(c => c.Kind == CriticalOperationKind.SettingsWrite));
        });
    }

    public static async Task TestPackageExportRegistersAsPackageExport()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var tracker = new RecordingOperationTracker();
            var settings = new PalworldSettingsService(logger);
            var rest = new PalworldRestClient(logger);
            var processes = new ServerProcessService(settings, rest, logger);
            var registry = new ProfileRegistry(paths, logger);
            var steamCmd = new SteamCmdService(paths, new SteamLocator(paths, logger), logger);
            var packages = new PortablePackageService(paths, processes, steamCmd, registry, logger, tracker);

            var profile = new ServerProfile { Name = "Export Test", InstallPath = Path.Combine(paths.ServersRoot, "export-test") };
            Directory.CreateDirectory(profile.SavedPath);
            await File.WriteAllTextAsync(Path.Combine(profile.SavedPath, "marker.txt"), "x");
            var output = Path.Combine(paths.Root, "out.palserver");

            await packages.ExportAsync(profile, output);
            Equal(1, tracker.Calls.Count(c => c.Kind == CriticalOperationKind.PackageExport));
        });
    }

    private static async Task WithTempPaths(Func<AppPaths, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "psm-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureCreated();
            await body(paths);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
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

    internal sealed class RecordingOperationTracker : ICriticalOperationTracker
    {
        private readonly CriticalOperationTracker _inner = new();
        public List<(CriticalOperationKind Kind, string? Detail)> Calls { get; } = [];

        public IDisposable Begin(CriticalOperationKind kind, string? detail = null)
        {
            Calls.Add((kind, detail));
            return _inner.Begin(kind, detail);
        }

        public bool IsBusy => _inner.IsBusy;
        public IReadOnlyList<string> ActiveOperations => _inner.ActiveOperations;
        public bool TryBeginShutdown(out string? blockReason) => _inner.TryBeginShutdown(out blockReason);
        public void CommitShutdown() => _inner.CommitShutdown();
        public void CancelShutdown() => _inner.CancelShutdown();
    }
}
