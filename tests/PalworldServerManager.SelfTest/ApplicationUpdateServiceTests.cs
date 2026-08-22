using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;
using PalworldServerManager.Core.Services;
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
            var service = CreateService(backend, paths, logger);
            service.SetChannel(UpdateChannel.Prerelease);

            var reloaded = CreateService(backend, paths, logger);
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

    public static async Task TestApplicationUpdateServiceHasNoPalworldRestClientDependency()
    {
        // 4E gives ApplicationUpdateService a ServerProcessService reference (needed for the
        // read-only BuildHandoffRecord call used to write the runtime handoff), so that alone is
        // no longer a useful safety boundary. What must remain structurally impossible is
        // talking to Palworld's REST API directly (Save/Shutdown) - and that requires a
        // PalworldRestClient, which this service must never hold, directly or transitively
        // through its own fields.
        var ctor = typeof(ApplicationUpdateService).GetConstructors().Single();
        var parameterTypeNames = ctor.GetParameters().Select(p => p.ParameterType.FullName).ToList();
        True(!parameterTypeNames.Any(n => n?.Contains("PalworldRestClient") == true), "ApplicationUpdateService must not depend on PalworldRestClient");

        var fieldTypeNames = typeof(ApplicationUpdateService)
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Select(f => f.FieldType.FullName);
        True(!fieldTypeNames.Any(n => n?.Contains("PalworldRestClient") == true), "ApplicationUpdateService must not hold a PalworldRestClient field");
        await Task.CompletedTask;
    }

    public static async Task TestApplyingDoesNotStopASyntheticRunningServer()
    {
        // Behavioral, not just structural: run a full ApplyAndRestartAsync against a real
        // ServerProcessService with a real (synthetic, harmless) process running under a managed
        // profile, and prove it is still alive and untouched afterward.
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var registry = new ProfileRegistry(paths, logger);
            var settings = new PalworldSettingsService(logger);
            var rest = new PalworldRestClient(logger);
            var processes = new ServerProcessService(settings, rest, logger);
            var handoff = new RuntimeHandoffService(paths, logger);
            var operations = new CriticalOperationTracker();

            var installPath = Path.Combine(paths.ServersRoot, Guid.NewGuid().ToString("N"), "PalServer");
            Directory.CreateDirectory(installPath);
            var profile = new ServerProfile { Name = "Apply Safety Test", InstallPath = installPath };
            await registry.AddAsync(profile);

            using var fake = SyntheticPalServerHarness.Start(installPath, waitSeconds: 10, exitCode: 0);
            try
            {
                var backend = new FakeUpdateBackend { ExecutionMode = UpdateExecutionMode.Installed };
                var service = new ApplicationUpdateService(backend, paths, logger, operations, registry, processes, handoff)
                {
                    PreRestartShutdownAsync = _ => Task.CompletedTask
                };

                backend.OnCheck = (_, _) => Task.FromResult(new UpdateCheckResult(true, new ReleaseInfo { Version = "9.9.9" }));
                await service.CheckForUpdatesAsync();
                await service.DownloadUpdateAsync();
                Equal(UpdateState.ReadyToInstall, service.Status.State);

                var result = await service.ApplyAndRestartAsync();
                True(result.Success, "apply should succeed against a real but idle ServerProcessService: " + result.Message);
                Equal(1, backend.ApplyCallCount);

                True(!fake.HasExited, "the synthetic running server must still be alive after ApplyAndRestartAsync");

                var handoffPath = Path.Combine(paths.RuntimeRoot, "update-handoff.json");
                var written = System.Text.Json.JsonSerializer.Deserialize<RuntimeHandoffDocument>(await File.ReadAllTextAsync(handoffPath));
                True(written is not null && written.Servers.Any(s => s.ProfileId == profile.Id), "the handoff must capture the running managed server");
            }
            finally { SyntheticPalServerHarness.TryKill(fake); }
        });
    }

    public static async Task TestApplyIsBlockedByEachCriticalOperationKindAndAllowedOnceIdle()
    {
        foreach (var kind in Enum.GetValues<CriticalOperationKind>())
        {
            await WithTempPaths(async paths =>
            {
                var logger = new FileLogger(paths);
                var tracker = new CriticalOperationTracker();
                var (service, backend) = await CreateReadyToInstallService(paths, logger, tracker);

                using (tracker.Begin(kind, "blocking op"))
                {
                    var reason = service.GetApplyBlockReason();
                    True(reason is not null && reason.Contains(kind.ToString()), $"GetApplyBlockReason should mention {kind}: {reason}");

                    var blocked = await service.ApplyAndRestartAsync();
                    True(!blocked.Success, $"apply must be blocked while a {kind} operation is active");
                    Equal(UpdateState.ReadyToInstall, service.Status.State);
                    Equal(0, backend.ApplyCallCount);
                }

                True(service.GetApplyBlockReason() is null, $"apply should be unblocked once the {kind} lease is released");
                var ok = await service.ApplyAndRestartAsync();
                True(ok.Success, $"apply should succeed once idle after a {kind} lease: {ok.Message}");
                Equal(1, backend.ApplyCallCount);
                Equal(UpdateState.Applying, service.Status.State);
            });
        }
    }

    public static async Task TestARunningServerAloneDoesNotBlockApply()
    {
        // Complements TestApplyingDoesNotStopASyntheticRunningServer: this asserts the block
        // reason itself, not just that apply ultimately succeeds.
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var tracker = new CriticalOperationTracker();
            var registry = new ProfileRegistry(paths, logger);
            var settings = new PalworldSettingsService(logger);
            var rest = new PalworldRestClient(logger);
            var processes = new ServerProcessService(settings, rest, logger, tracker);
            var installPath = Path.Combine(paths.ServersRoot, Guid.NewGuid().ToString("N"), "PalServer");
            var profile = new ServerProfile { Name = "Running Alone Test", InstallPath = installPath };
            await registry.AddAsync(profile);

            using var fake = SyntheticPalServerHarness.Start(installPath, waitSeconds: 10, exitCode: 0);
            try
            {
                var handoff = new RuntimeHandoffService(paths, logger);
                var backend = new FakeUpdateBackend
                {
                    ExecutionMode = UpdateExecutionMode.Installed,
                    OnCheck = (_, _) => Task.FromResult(new UpdateCheckResult(true, new ReleaseInfo { Version = "2.0.0" }))
                };
                var service = new ApplicationUpdateService(backend, paths, logger, tracker, registry, processes, handoff)
                {
                    PreRestartShutdownAsync = _ => Task.CompletedTask
                };
                await service.CheckForUpdatesAsync();
                await service.DownloadUpdateAsync();

                True(processes.IsRunning(profile), "the synthetic process must be observed as running for this to be a meaningful test");
                True(service.GetApplyBlockReason() is null, "a running Palworld server, with no active critical operation, must not block apply");
            }
            finally { SyntheticPalServerHarness.TryKill(fake); }
        });
    }

    public static async Task TestFailedHandoffWriteLeavesStateReadyToInstall()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var tracker = new CriticalOperationTracker();
            var (service, _) = await CreateReadyToInstallService(paths, logger, tracker);

            // Make the handoff file's directory unwritable-in-effect by occupying its exact path
            // with a directory instead of allowing a file: forces RuntimeHandoffService.WriteAsync
            // to fail deterministically without relying on real filesystem permission APIs.
            var handoffPath = Path.Combine(paths.RuntimeRoot, "update-handoff.json");
            Directory.CreateDirectory(handoffPath);

            var result = await service.ApplyAndRestartAsync();
            True(!result.Success, "apply must fail if the runtime handoff cannot be written");
            Equal(UpdateState.ReadyToInstall, service.Status.State);
            True(service.Status.ErrorMessage is not null, "a failed apply must leave an actionable error message");
            True(!tracker.IsBusy, "the shutdown gate must be rolled back after a failed handoff write");

            // And the service must still be usable afterward.
            Directory.Delete(handoffPath);
            var retry = await service.ApplyAndRestartAsync();
            True(retry.Success, "apply must be retryable after the underlying problem is fixed: " + retry.Message);
        });
    }

    public static async Task TestFailedBackendApplyCallTriggersRecovery()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var tracker = new CriticalOperationTracker();
            var registry = new ProfileRegistry(paths, logger);
            var settings = new PalworldSettingsService(logger);
            var rest = new PalworldRestClient(logger);
            var processes = new ServerProcessService(settings, rest, logger);
            var handoff = new RuntimeHandoffService(paths, logger);
            var backend = new FakeUpdateBackend
            {
                ExecutionMode = UpdateExecutionMode.Installed,
                OnCheck = (_, _) => Task.FromResult(new UpdateCheckResult(true, new ReleaseInfo { Version = "2.0.0" })),
                OnApply = _ => throw new InvalidOperationException("synthetic updater launch failure")
            };
            var recoveryRan = false;
            var service = new ApplicationUpdateService(backend, paths, logger, tracker, registry, processes, handoff)
            {
                PreRestartShutdownAsync = _ => Task.CompletedTask,
                PostFailureRecoveryAsync = _ => { recoveryRan = true; return Task.CompletedTask; }
            };
            await service.CheckForUpdatesAsync();
            await service.DownloadUpdateAsync();

            var result = await service.ApplyAndRestartAsync();
            True(!result.Success, "apply must report failure when the backend's apply call itself throws");
            Equal(UpdateState.ReadyToInstall, service.Status.State);
            True(recoveryRan, "PostFailureRecoveryAsync must run to resume Manager-only services since PreRestartShutdownAsync already ran before the backend call failed");
            True(!tracker.IsBusy, "the shutdown gate must be rolled back after a failed backend apply call");
        });
    }

    public static async Task TestProfileLoadFailureRollsBackAfterShutdownGateAcquired()
    {
        // Finding 1: before this fix, only the handoff-write and backend-apply steps were
        // explicitly wrapped in rollback logic. A real, deterministic failure earlier in the
        // pipeline - a corrupt server registry file - must roll back exactly the same way: gate
        // released, state ReadyToInstall, no stale handoff, retryable.
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var tracker = new CriticalOperationTracker();
            var (service, backend) = await CreateReadyToInstallService(paths, logger, tracker);

            await File.WriteAllTextAsync(paths.ProfilesFile, "{ not valid json ]");

            var result = await service.ApplyAndRestartAsync();
            True(!result.Success, "apply must fail when the server registry cannot be loaded");
            Equal(UpdateState.ReadyToInstall, service.Status.State);
            Equal(0, backend.ApplyCallCount);
            True(!tracker.IsBusy, "the shutdown gate must be rolled back after a profile-load failure");

            var handoffPath = Path.Combine(paths.RuntimeRoot, "update-handoff.json");
            True(!File.Exists(handoffPath), "no handoff should exist - the failure happened before one was ever written");

            await File.WriteAllTextAsync(paths.ProfilesFile, "[]");
            var retry = await service.ApplyAndRestartAsync();
            True(retry.Success, "apply must be retryable once the registry is readable again: " + retry.Message);
        });
    }

    public static async Task TestCancellationAfterShutdownGateRollsBackTheSameWayAsAnyOtherFailure()
    {
        // OperationCanceledException gets its own catch clause, distinct from the general
        // Exception clause, so it must be proven separately that it drives the identical
        // rollback - gate canceled, handoff deleted, state ReadyToInstall - rather than escaping
        // uncaught the way it could before Finding 1's fix.
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var tracker = new CriticalOperationTracker();
            var registry = new ProfileRegistry(paths, logger);
            var settings = new PalworldSettingsService(logger);
            var rest = new PalworldRestClient(logger);
            var processes = new ServerProcessService(settings, rest, logger);
            var handoff = new RuntimeHandoffService(paths, logger);
            var backend = new FakeUpdateBackend
            {
                ExecutionMode = UpdateExecutionMode.Installed,
                OnCheck = (_, _) => Task.FromResult(new UpdateCheckResult(true, new ReleaseInfo { Version = "2.0.0" })),
                OnApply = _ => throw new OperationCanceledException("synthetic cancellation after the shutdown gate and handoff were already committed")
            };
            var service = new ApplicationUpdateService(backend, paths, logger, tracker, registry, processes, handoff)
            {
                PreRestartShutdownAsync = _ => Task.CompletedTask
            };
            await service.CheckForUpdatesAsync();
            await service.DownloadUpdateAsync();

            var result = await service.ApplyAndRestartAsync();
            True(!result.Success, "a cancellation after the shutdown gate was acquired must still be reported as a failure, not silently swallowed");
            Equal(UpdateState.ReadyToInstall, service.Status.State);
            True(!tracker.IsBusy, "the shutdown gate must be rolled back after a cancellation");

            var handoffPath = Path.Combine(paths.RuntimeRoot, "update-handoff.json");
            True(!File.Exists(handoffPath), "the handoff that was written before the cancellation must be discarded, not left behind");
        });
    }

    public static async Task TestFailedBackendApplyCallDeletesTheHandoffFile()
    {
        // Finding 3: the handoff must exist only for a restart that actually committed to a
        // launched updater. If BeginApplyAndRestart throws after the handoff was already
        // written, the file must not survive the failed attempt - otherwise a later, unrelated
        // normal Manager restart within the 5-minute staleness window could wrongly consume it.
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var tracker = new CriticalOperationTracker();
            var (service, backend) = await CreateReadyToInstallService(paths, logger, tracker);
            backend.OnApply = _ => throw new InvalidOperationException("synthetic updater launch failure");

            var handoffPath = Path.Combine(paths.RuntimeRoot, "update-handoff.json");
            var result = await service.ApplyAndRestartAsync();

            True(!result.Success, "apply must fail when the backend's apply call throws");
            True(!File.Exists(handoffPath), "a handoff written for a failed apply attempt must be deleted, not left for the staleness window to eventually expire");
        });
    }

    public static async Task TestApplyEligibilityNotifiesWhenABlockingOperationBeginsAndEnds()
    {
        // Finding 2: a listener relying solely on StatusChanged (e.g. UpdatesWindow) needs
        // ApplicationUpdateService to re-raise it whenever ICriticalOperationTracker.Changed
        // fires - not just when the service's own state changes - so the UI reflects another
        // operation's lease releasing without being closed and reopened.
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var tracker = new CriticalOperationTracker();
            var (service, _) = await CreateReadyToInstallService(paths, logger, tracker);

            var notified = false;
            service.StatusChanged += (_, _) => notified = true;

            True(service.GetApplyBlockReason() is null, "nothing should block apply yet");

            var lease = tracker.Begin(CriticalOperationKind.Backup, "unrelated backup");
            True(notified, "a critical operation beginning elsewhere must notify listeners that apply eligibility may have changed");
            True(service.GetApplyBlockReason() is not null && service.GetApplyBlockReason()!.Contains("Backup"), "the blocker should now name the active operation");

            notified = false;
            lease.Dispose();
            True(notified, "the operation ending must also notify listeners so a stale blocker message does not linger");
            True(service.GetApplyBlockReason() is null, "apply should be unblocked again now that the operation lease was released - without reopening anything");
        });
    }

    public static async Task TestApplyEligibilityNotifiesWhenTheShutdownGateIsCanceled()
    {
        // The shutdown gate (TryBeginShutdown/CancelShutdown) is a second, independent source of
        // eligibility changes distinct from ordinary operation leases - a failed/canceled apply
        // attempt cancels the gate as part of rollback, and listeners must be told apply may be
        // available again.
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var tracker = new CriticalOperationTracker();
            var (service, _) = await CreateReadyToInstallService(paths, logger, tracker);

            True(tracker.TryBeginShutdown(out _), "the gate should acquire cleanly on an idle tracker");

            var notified = false;
            service.StatusChanged += (_, _) => notified = true;

            tracker.CancelShutdown();
            True(notified, "canceling the shutdown gate must notify ApplicationUpdateService so the UI can recover without being reopened");
        });
    }

    public static async Task TestConcurrentApplyAttemptsAreRejected()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var tracker = new CriticalOperationTracker();
            var registry = new ProfileRegistry(paths, logger);
            var settings = new PalworldSettingsService(logger);
            var rest = new PalworldRestClient(logger);
            var processes = new ServerProcessService(settings, rest, logger);
            var handoff = new RuntimeHandoffService(paths, logger);
            var backend = new FakeUpdateBackend
            {
                ExecutionMode = UpdateExecutionMode.Installed,
                OnCheck = (_, _) => Task.FromResult(new UpdateCheckResult(true, new ReleaseInfo { Version = "2.0.0" }))
            };
            var release = new TaskCompletionSource();
            var service = new ApplicationUpdateService(backend, paths, logger, tracker, registry, processes, handoff)
            {
                // Stalls the first apply call mid-flight (gate already held, shutdown already
                // committed) so a second concurrent call has something real to collide with.
                PreRestartShutdownAsync = _ => release.Task
            };
            await service.CheckForUpdatesAsync();
            await service.DownloadUpdateAsync();

            var first = service.ApplyAndRestartAsync();
            await Task.Delay(50); // let the first call actually enter Applying and take the gate
            var second = await service.ApplyAndRestartAsync();

            True(!second.Success, "a second concurrent apply attempt must be rejected, not queued");
            release.SetResult();
            var firstResult = await first;
            True(firstResult.Success, "the first apply attempt should still succeed: " + firstResult.Message);
            Equal(1, backend.ApplyCallCount);
        });
    }

    public static async Task TestApplyRequiresReadyToInstallState()
    {
        await WithService(UpdateExecutionMode.Installed, async (service, backend) =>
        {
            // Idle: nothing downloaded yet.
            var result = await service.ApplyAndRestartAsync();
            True(!result.Success, "apply must fail with no update staged");
            Equal(0, backend.ApplyCallCount);
        });
    }

    private static async Task<(ApplicationUpdateService Service, FakeUpdateBackend Backend)> CreateReadyToInstallService(AppPaths paths, IAppLogger logger, ICriticalOperationTracker tracker)
    {
        var registry = new ProfileRegistry(paths, logger);
        var settings = new PalworldSettingsService(logger);
        var rest = new PalworldRestClient(logger);
        var processes = new ServerProcessService(settings, rest, logger);
        var handoff = new RuntimeHandoffService(paths, logger);
        var backend = new FakeUpdateBackend
        {
            ExecutionMode = UpdateExecutionMode.Installed,
            OnCheck = (_, _) => Task.FromResult(new UpdateCheckResult(true, new ReleaseInfo { Version = "2.0.0" }))
        };
        var service = new ApplicationUpdateService(backend, paths, logger, tracker, registry, processes, handoff)
        {
            PreRestartShutdownAsync = _ => Task.CompletedTask
        };
        await service.CheckForUpdatesAsync();
        await service.DownloadUpdateAsync();
        return (service, backend);
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
            var service = CreateService(backend, paths, logger);

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
            var service = CreateService(backend, paths, logger);
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
            var service = CreateService(backend, paths, logger);
            await body(service, backend);
        });
    }

    private static ApplicationUpdateService CreateService(FakeUpdateBackend backend, AppPaths paths, IAppLogger logger)
    {
        var registry = new ProfileRegistry(paths, logger);
        var settings = new PalworldSettingsService(logger);
        var rest = new PalworldRestClient(logger);
        var processes = new ServerProcessService(settings, rest, logger);
        var handoff = new RuntimeHandoffService(paths, logger);
        return new ApplicationUpdateService(backend, paths, logger, new CriticalOperationTracker(), registry, processes, handoff);
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
        public Action<ReleaseInfo>? OnApply { get; set; }
        public UpdateChannel? LastCheckedChannel { get; private set; }
        public int CheckCallCount { get; private set; }
        public int DownloadCallCount { get; private set; }
        public int ApplyCallCount { get; private set; }

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

        public void BeginApplyAndRestart(ReleaseInfo release)
        {
            ApplyCallCount++;
            OnApply?.Invoke(release);
        }
    }
}
