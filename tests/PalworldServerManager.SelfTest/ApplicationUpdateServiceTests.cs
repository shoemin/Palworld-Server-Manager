using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;
using PalworldServerManager.Core.Services.Update;

namespace PalworldServerManager.SelfTest;

internal static class ApplicationUpdateServiceTests
{
    // ---- Execution mode detection (pure logic) ---------------------------------------------

    public static Task TestExecutionModeDetectorPrefersInstalledOverEverything()
    {
        Equal(UpdateExecutionMode.Installed, UpdateExecutionModeDetector.Detect(true, true, @"C:\anything"));
        return Task.CompletedTask;
    }

    public static Task TestExecutionModeDetectorRecognizesVelopackPortable()
    {
        Equal(UpdateExecutionMode.Portable, UpdateExecutionModeDetector.Detect(false, true, @"C:\anything"));
        return Task.CompletedTask;
    }

    public static async Task TestExecutionModeDetectorRecognizesDevelopmentBuildBySiblingCsproj()
    {
        var root = Path.Combine(Path.GetTempPath(), "psm-selftest-" + Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(root, "src", "PalworldServerManager.App");
        var binDir = Path.Combine(projectDir, "bin", "Release", "net8.0-windows");
        Directory.CreateDirectory(binDir);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "PalworldServerManager.App.csproj"), "<Project/>");
        try
        {
            Equal(UpdateExecutionMode.Development, UpdateExecutionModeDetector.Detect(false, false, binDir));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    public static Task TestExecutionModeDetectorDefaultsToPortableWhenAmbiguous()
    {
        // A relocated/renamed folder with no sibling .csproj must not be mistaken for a dev build.
        Equal(UpdateExecutionMode.Portable, UpdateExecutionModeDetector.Detect(false, false, @"C:\Users\Someone\Downloads\PalworldServerManager"));
        // Even a folder literally named bin\Release without a real project file nearby stays Portable.
        Equal(UpdateExecutionMode.Portable, UpdateExecutionModeDetector.Detect(false, false, @"C:\Users\Someone\Downloads\bin\Release"));
        return Task.CompletedTask;
    }

    // ---- Execution-mode gating in the service ------------------------------------------------

    public static async Task TestCheckIsSkippedWhenNotInstalled()
    {
        await WithService(UpdateExecutionMode.Portable, async (service, backend) =>
        {
            await service.CheckForUpdatesAsync();
            Equal(0, backend.CheckCallCount);
            Equal(UpdateState.Idle, service.Status.State);
        });

        await WithService(UpdateExecutionMode.Development, async (service, backend) =>
        {
            await service.CheckForUpdatesAsync();
            Equal(0, backend.CheckCallCount);
        });
    }

    // ---- Channels ------------------------------------------------------------------------

    public static async Task TestDefaultChannelIsStable()
    {
        await WithService(UpdateExecutionMode.Installed, (service, _) =>
        {
            Equal(UpdateChannel.Stable, service.Status.Channel);
            return Task.CompletedTask;
        });
    }

    public static async Task TestChannelPersistsAcrossServiceInstances()
    {
        await WithTempPaths(paths =>
        {
            var logger = new FileLogger(paths);
            var backend = new FakeUpdateBackend { ExecutionMode = UpdateExecutionMode.Installed };
            var service = new ApplicationUpdateService(backend, paths, logger);
            service.SetChannel(UpdateChannel.Prerelease);

            var reloaded = new ApplicationUpdateService(backend, paths, logger);
            Equal(UpdateChannel.Prerelease, reloaded.Status.Channel);
            return Task.CompletedTask;
        });
    }

    public static async Task TestChangingChannelInvalidatesCachedAvailability()
    {
        await WithService(UpdateExecutionMode.Installed, async (service, backend) =>
        {
            backend.OnCheck = (_, _) => Task.FromResult(new UpdateCheckResult(true, new ReleaseInfo { Version = "9.9.9" }));
            await service.CheckForUpdatesAsync();
            Equal(UpdateState.UpdateAvailable, service.Status.State);

            service.SetChannel(UpdateChannel.Prerelease);
            Equal(UpdateState.Idle, service.Status.State);
            True(service.Status.AvailableRelease is null, "switching channels must clear a cached available release");
        });
    }

    public static async Task TestCheckPassesTheCurrentlySelectedChannelToTheBackend()
    {
        await WithService(UpdateExecutionMode.Installed, async (service, backend) =>
        {
            backend.OnCheck = (channel, _) => Task.FromResult(
                channel == UpdateChannel.Prerelease
                    ? new UpdateCheckResult(true, new ReleaseInfo { Version = "1.0.0-beta.1" })
                    : new UpdateCheckResult(false, null));

            await service.CheckForUpdatesAsync();
            Equal(UpdateChannel.Stable, backend.LastCheckedChannel);
            Equal(UpdateState.Idle, service.Status.State);

            service.SetChannel(UpdateChannel.Prerelease);
            await service.CheckForUpdatesAsync();
            Equal(UpdateChannel.Prerelease, backend.LastCheckedChannel);
            Equal(UpdateState.UpdateAvailable, service.Status.State);
            Equal("1.0.0-beta.1", service.Status.AvailableRelease?.Version);
        });
    }

    // ---- State machine ---------------------------------------------------------------------

    public static async Task TestIdleCheckingIdleWhenNoUpdateFound()
    {
        await WithService(UpdateExecutionMode.Installed, async (service, backend) =>
        {
            Equal(UpdateState.Idle, service.Status.State);
            backend.OnCheck = (_, _) => Task.FromResult(new UpdateCheckResult(false, null));
            await service.CheckForUpdatesAsync();
            Equal(UpdateState.Idle, service.Status.State);
            True(service.Status.LastCheckedUtc is not null, "a completed check must record LastCheckedUtc even when no update was found");
        });
    }

    public static async Task TestIdleCheckingUpdateAvailable()
    {
        await WithService(UpdateExecutionMode.Installed, async (service, backend) =>
        {
            backend.OnCheck = (_, _) => Task.FromResult(new UpdateCheckResult(true, new ReleaseInfo { Version = "2.0.0", SizeBytes = 123 }));
            await service.CheckForUpdatesAsync();
            Equal(UpdateState.UpdateAvailable, service.Status.State);
            Equal("2.0.0", service.Status.AvailableRelease?.Version);
        });
    }

    public static async Task TestUpdateAvailableDownloadingReadyToInstall()
    {
        await WithService(UpdateExecutionMode.Installed, async (service, backend) =>
        {
            backend.OnCheck = (_, _) => Task.FromResult(new UpdateCheckResult(true, new ReleaseInfo { Version = "2.0.0" }));
            await service.CheckForUpdatesAsync();

            backend.OnDownload = async (_, progress, _) =>
            {
                progress.Report(50);
                await Task.Yield();
                progress.Report(100);
            };
            await service.DownloadUpdateAsync();

            Equal(UpdateState.ReadyToInstall, service.Status.State);
            Equal(1, backend.DownloadCallCount);
        });
    }

    public static async Task TestDownloadWithNothingStagedIsANoOp()
    {
        await WithService(UpdateExecutionMode.Installed, async (service, backend) =>
        {
            await service.DownloadUpdateAsync();
            Equal(0, backend.DownloadCallCount);
            Equal(UpdateState.Idle, service.Status.State);
        });
    }

    public static async Task TestCheckFailureTransitionsToFailed()
    {
        await WithService(UpdateExecutionMode.Installed, async (service, backend) =>
        {
            backend.OnCheck = (_, _) => throw new InvalidOperationException("synthetic check failure");
            await service.CheckForUpdatesAsync();
            Equal(UpdateState.Failed, service.Status.State);
            True(service.Status.ErrorMessage is not null, "a failed check must leave a user-facing error message");
        });
    }

    public static async Task TestDownloadFailureTransitionsToFailed()
    {
        await WithService(UpdateExecutionMode.Installed, async (service, backend) =>
        {
            backend.OnCheck = (_, _) => Task.FromResult(new UpdateCheckResult(true, new ReleaseInfo { Version = "2.0.0" }));
            await service.CheckForUpdatesAsync();

            backend.OnDownload = (_, _, _) => throw new InvalidOperationException("synthetic download failure");
            await service.DownloadUpdateAsync();
            Equal(UpdateState.Failed, service.Status.State);
        });
    }

    public static async Task TestRetryFromFailedSucceeds()
    {
        await WithService(UpdateExecutionMode.Installed, async (service, backend) =>
        {
            backend.OnCheck = (_, _) => throw new InvalidOperationException("synthetic failure");
            await service.CheckForUpdatesAsync();
            Equal(UpdateState.Failed, service.Status.State);

            backend.OnCheck = (_, _) => Task.FromResult(new UpdateCheckResult(false, null));
            await service.CheckForUpdatesAsync();
            Equal(UpdateState.Idle, service.Status.State);
        });
    }

    public static async Task TestOverlappingCheckIsRejectedNotQueued()
    {
        await WithService(UpdateExecutionMode.Installed, async (service, backend) =>
        {
            var release = new TaskCompletionSource();
            backend.OnCheck = async (_, _) =>
            {
                await release.Task;
                return new UpdateCheckResult(false, null);
            };

            var first = service.CheckForUpdatesAsync();
            await Task.Delay(50); // let the first call actually enter Checking and take the gate
            var second = service.CheckForUpdatesAsync();
            await second; // the second call must return immediately without waiting on the gate

            Equal(1, backend.CheckCallCount);
            Equal(UpdateState.Checking, service.Status.State);
            release.SetResult();
            await first;
        });
    }

    public static async Task TestOverlappingDownloadIsRejectedNotQueued()
    {
        await WithService(UpdateExecutionMode.Installed, async (service, backend) =>
        {
            backend.OnCheck = (_, _) => Task.FromResult(new UpdateCheckResult(true, new ReleaseInfo { Version = "2.0.0" }));
            await service.CheckForUpdatesAsync();

            var release = new TaskCompletionSource();
            backend.OnDownload = async (_, _, _) => await release.Task;

            var first = service.DownloadUpdateAsync();
            await Task.Delay(50);
            var second = service.DownloadUpdateAsync();
            await second;

            Equal(1, backend.DownloadCallCount);
            release.SetResult();
            await first;
        });
    }

    // ---- Safety: Palworld/server lifetime is never touched -----------------------------------

    public static async Task TestApplicationUpdateServiceHasNoServerProcessServiceDependency()
    {
        // Structural guarantee, not just a runtime observation: this service cannot invoke
        // ServerProcessService.Stop/Force-stop/etc. because it never holds a reference to one.
        var ctor = typeof(ApplicationUpdateService).GetConstructors().Single();
        var parameterTypeNames = ctor.GetParameters().Select(p => p.ParameterType.FullName).ToList();
        True(!parameterTypeNames.Any(n => n?.Contains("ServerProcessService") == true), "ApplicationUpdateService must not depend on ServerProcessService");

        var fieldTypeNames = typeof(ApplicationUpdateService)
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Select(f => f.FieldType.FullName);
        True(!fieldTypeNames.Any(n => n?.Contains("ServerProcessService") == true), "ApplicationUpdateService must not hold a ServerProcessService field");
        await Task.CompletedTask;
    }

    public static async Task TestCheckingAndDownloadingNeverWriteARuntimeHandoff()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var backend = new FakeUpdateBackend
            {
                ExecutionMode = UpdateExecutionMode.Installed,
                OnCheck = (_, _) => Task.FromResult(new UpdateCheckResult(true, new ReleaseInfo { Version = "2.0.0" }))
            };
            var service = new ApplicationUpdateService(backend, paths, logger);

            await service.CheckForUpdatesAsync();
            await service.DownloadUpdateAsync();

            Equal(UpdateState.ReadyToInstall, service.Status.State);
            var handoffPath = Path.Combine(paths.RuntimeRoot, "update-handoff.json");
            True(!File.Exists(handoffPath), "4D must never write a runtime handoff; that is 4E's responsibility");
        });
    }

    // ---- Logging -----------------------------------------------------------------------------

    public static async Task TestCheckFailureIsLoggedAsAnError()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var backend = new FakeUpdateBackend
            {
                ExecutionMode = UpdateExecutionMode.Installed,
                OnCheck = (_, _) => throw new InvalidOperationException("synthetic failure for log verification")
            };
            var service = new ApplicationUpdateService(backend, paths, logger);
            await service.CheckForUpdatesAsync();

            var text = await File.ReadAllTextAsync(logger.CurrentLogFile);
            True(text.Contains("Update check failed"), "a failed check must be logged");
        });
    }

    // ---- helpers -------------------------------------------------------------------------

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

    private static async Task WithService(UpdateExecutionMode mode, Func<ApplicationUpdateService, FakeUpdateBackend, Task> body)
    {
        var backend = new FakeUpdateBackend { ExecutionMode = mode };
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var service = new ApplicationUpdateService(backend, paths, logger);
            await body(service, backend);
        });
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

    private sealed class FakeUpdateBackend : IApplicationUpdateBackend
    {
        public UpdateExecutionMode ExecutionMode { get; set; } = UpdateExecutionMode.Installed;
        public string CurrentVersion { get; set; } = "0.3.0";
        public Func<UpdateChannel, CancellationToken, Task<UpdateCheckResult>>? OnCheck { get; set; }
        public Func<ReleaseInfo, IProgress<int>, CancellationToken, Task>? OnDownload { get; set; }
        public UpdateChannel? LastCheckedChannel { get; private set; }
        public int CheckCallCount { get; private set; }
        public int DownloadCallCount { get; private set; }

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(UpdateChannel channel, CancellationToken cancellationToken)
        {
            LastCheckedChannel = channel;
            CheckCallCount++;
            if (OnCheck is not null) return await OnCheck(channel, cancellationToken);
            return new UpdateCheckResult(false, null);
        }

        public async Task DownloadUpdatesAsync(ReleaseInfo release, IProgress<int> progress, CancellationToken cancellationToken)
        {
            DownloadCallCount++;
            if (OnDownload is not null) { await OnDownload(release, progress, cancellationToken); return; }
            progress.Report(100);
        }
    }
}
