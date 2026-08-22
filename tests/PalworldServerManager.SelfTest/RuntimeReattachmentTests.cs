using System.Diagnostics;
using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;
using PalworldServerManager.Core.Services;

namespace PalworldServerManager.SelfTest;

internal static class RuntimeReattachmentTests
{
    // ---- Pure identity matching (no real process required) ------------------------------

    public static Task TestIdentityMatcherRejectsPidReuseAcrossStartTimeMismatch()
    {
        var hint = new RuntimeHandoffProcessRecord
        {
            ProcessId = 4242,
            ProcessName = "PalServer",
            ExecutablePath = @"C:\Servers\Alpha\PalServer.exe",
            StartTimeUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
        };
        // Same PID, same path, but a very different start time: Windows reused this PID for an unrelated process.
        var reused = new ProcessDescriptor(4242, "PalServer", @"C:\Servers\Alpha\PalServer.exe", new DateTime(2026, 1, 1, 12, 45, 0, DateTimeKind.Utc));

        True(!ProcessIdentityMatcher.IsSafeIdentityMatch(reused, @"C:\Servers\Alpha", hint), "a start-time mismatch must be treated as PID reuse, not a match");
        return Task.CompletedTask;
    }

    public static Task TestIdentityMatcherRejectsExecutablePathMismatch()
    {
        var hint = new RuntimeHandoffProcessRecord
        {
            ProcessId = 100,
            ProcessName = "PalServer",
            ExecutablePath = @"C:\Servers\Alpha\PalServer.exe",
            StartTimeUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
        };
        // Right PID, right start time, but the executable belongs to a different managed install.
        var wrongInstall = new ProcessDescriptor(100, "PalServer", @"C:\Servers\Beta\PalServer.exe", hint.StartTimeUtc);

        True(!ProcessIdentityMatcher.IsSafeIdentityMatch(wrongInstall, @"C:\Servers\Alpha", hint), "an executable path outside the expected install must be rejected");
        return Task.CompletedTask;
    }

    public static Task TestIdentityMatcherRejectsUnrecognizedProcessName()
    {
        var hint = new RuntimeHandoffProcessRecord
        {
            ProcessId = 100,
            ProcessName = "PalServer",
            ExecutablePath = @"C:\Servers\Alpha\PalServer.exe",
            StartTimeUtc = null
        };
        var notPalServer = new ProcessDescriptor(100, "notepad", @"C:\Servers\Alpha\PalServer.exe", null);

        True(!ProcessIdentityMatcher.IsSafeIdentityMatch(notPalServer, @"C:\Servers\Alpha", hint), "a process whose name is not a recognized PalServer process must never be treated as one merely because a PID/path lined up");
        return Task.CompletedTask;
    }

    public static Task TestIdentityMatcherAcceptsFullyVerifiedMatch()
    {
        var start = DateTime.UtcNow;
        var hint = new RuntimeHandoffProcessRecord
        {
            ProcessId = 777,
            ProcessName = "PalServer-Win64-Shipping-Cmd",
            ExecutablePath = @"C:\Servers\Alpha\PalServer.exe",
            StartTimeUtc = start
        };
        var candidate = new ProcessDescriptor(777, "PalServer-Win64-Shipping-Cmd", @"C:\Servers\Alpha\PalServer.exe", start.AddMilliseconds(400));

        True(ProcessIdentityMatcher.IsSafeIdentityMatch(candidate, @"C:\Servers\Alpha", hint), "PID + name + path + start time within tolerance must verify");
        return Task.CompletedTask;
    }

    // ---- Runtime handoff persistence -----------------------------------------------------

    public static async Task TestRuntimeHandoffRoundTripsAndIsOneShot()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var service = new RuntimeHandoffService(paths, logger);
            var profileId = Guid.NewGuid();
            var document = new RuntimeHandoffDocument
            {
                OldManagerVersion = "0.4.0",
                TargetManagerVersion = "0.4.1",
                Servers =
                [
                    new RuntimeHandoffServerRecord
                    {
                        ProfileId = profileId,
                        ProfileName = "Test Server",
                        InstallPath = Path.Combine(paths.ServersRoot, "test"),
                        Processes = [new RuntimeHandoffProcessRecord { ProcessId = 123, ProcessName = "PalServer", ExecutablePath = "x", StartTimeUtc = DateTime.UtcNow }]
                    }
                ]
            };

            await service.WriteAsync(document);
            var consumed = await service.ConsumeAsync();
            True(consumed is not null, "a freshly written handoff must be consumable");
            Equal(profileId, consumed!.Servers[0].ProfileId);

            var consumedAgain = await service.ConsumeAsync();
            True(consumedAgain is null, "consuming a handoff must be one-shot; the file must not be reusable");
        });
    }

    public static async Task TestRuntimeHandoffContainsNoSecretShapedFields()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var service = new RuntimeHandoffService(paths, logger);
            var document = new RuntimeHandoffDocument
            {
                Servers = [new RuntimeHandoffServerRecord { ProfileId = Guid.NewGuid(), ProfileName = "Test", InstallPath = "x" }]
            };
            await service.WriteAsync(document);

            var raw = await File.ReadAllTextAsync(Path.Combine(paths.RuntimeRoot, "update-handoff.json"));
            True(!raw.Contains("password", StringComparison.OrdinalIgnoreCase), "handoff file must never contain password-shaped content");
            True(!raw.Contains("adminpassword", StringComparison.OrdinalIgnoreCase), "handoff file must never contain AdminPassword");
            True(!raw.Contains("token", StringComparison.OrdinalIgnoreCase), "handoff file must never contain token-shaped content");
        });
    }

    public static async Task TestRuntimeHandoffRejectsStaleFile()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var service = new RuntimeHandoffService(paths, logger);
            var document = new RuntimeHandoffDocument
            {
                CreatedUtc = DateTime.UtcNow.AddHours(-1),
                Servers = []
            };
            await service.WriteAsync(document);

            var consumed = await service.ConsumeAsync();
            True(consumed is null, "an hour-old handoff must be treated as stale and discarded rather than trusted");
        });
    }

    public static async Task TestRuntimeHandoffDeleteAsyncIsSafeAndIdempotentWhenNoFileExists()
    {
        // Finding 3's rollback cleanup calls DeleteAsync unconditionally whenever a handoff was
        // written, including on a retry after an earlier rollback already deleted it - it must
        // never throw for the ordinary "nothing to delete" case.
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var service = new RuntimeHandoffService(paths, logger);
            var handoffPath = Path.Combine(paths.RuntimeRoot, "update-handoff.json");
            True(!File.Exists(handoffPath), "no handoff should exist yet in a fresh temp environment");

            await service.DeleteAsync();
            await service.DeleteAsync();
            True(!File.Exists(handoffPath), "DeleteAsync must remain a no-op when there is nothing to delete");
        });
    }

    public static async Task TestRuntimeHandoffDeleteAsyncRemovesOnlyTheHandoffFile()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var service = new RuntimeHandoffService(paths, logger);
            await service.WriteAsync(new RuntimeHandoffDocument { Servers = [] });
            var handoffPath = Path.Combine(paths.RuntimeRoot, "update-handoff.json");
            True(File.Exists(handoffPath), "the handoff should exist after WriteAsync for this test to be meaningful");

            var unrelatedMarker = Path.Combine(paths.RuntimeRoot, "unrelated.txt");
            await File.WriteAllTextAsync(unrelatedMarker, "must survive");

            await service.DeleteAsync();

            True(!File.Exists(handoffPath), "DeleteAsync must remove the handoff file");
            True(File.Exists(unrelatedMarker), "DeleteAsync must never touch anything other than the handoff file itself");
        });
    }

    public static async Task TestRuntimeHandoffRejectsUnsupportedFormatVersion()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var service = new RuntimeHandoffService(paths, logger);
            var document = new RuntimeHandoffDocument { FormatVersion = 99, Servers = [] };
            await service.WriteAsync(document);

            var consumed = await service.ConsumeAsync();
            True(consumed is null, "an unsupported handoff format version must be rejected rather than partially trusted");
        });
    }

    // ---- End-to-end reconciliation against a real (synthetic) process --------------------

    public static async Task TestReconcileAttachesToAlreadyRunningProcessAndCapturesExitCode()
    {
        await WithSyntheticEnvironment(async (paths, logger, processes) =>
        {
            var profile = NewProfile(paths, "Reattach Exit Test");
            using var fake = SyntheticPalServerHarness.Start(profile.InstallPath, waitSeconds: 2, exitCode: 5);
            try
            {
                var ended = new TaskCompletionSource<ServerProcessLifetimeEndedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                processes.ServerLifetimeEnded += (_, e) => { if (e.ServerId == profile.Id) ended.TrySetResult(e); };

                var outcome = await processes.ReconcileAsync(profile, hint: null);
                Equal(ReconcileOutcome.Attached, outcome);
                True(processes.IsOwnedLifetimeActive(profile), "a reconciled server must be tracked as an owned lifetime");
                Equal("Running (monitored)", processes.GetStatusText(profile));

                var result = await ended.Task.WaitAsync(TimeSpan.FromSeconds(20));
                True(!result.ExpectedStop, "a synthetic process exiting on its own is not an expected/manager-requested stop");
                True(result.HasNonZeroExitCode, "exit code 5 must be classified as non-zero");
                Equal(5, result.PrimaryExitCode);
            }
            finally { SyntheticPalServerHarness.TryKill(fake); }
        });
    }

    public static async Task TestReconcileFallsBackToPathScanWhenHandoffHintDoesNotVerify()
    {
        await WithSyntheticEnvironment(async (paths, logger, processes) =>
        {
            var profile = NewProfile(paths, "Stale Hint Test");
            using var fake = SyntheticPalServerHarness.Start(profile.InstallPath, waitSeconds: 5, exitCode: 0);
            try
            {
                // A hint with the right PID but an implausible start time simulates PID reuse / a stale observation.
                var badHint = new RuntimeHandoffServerRecord
                {
                    ProfileId = profile.Id,
                    ProfileName = profile.Name,
                    InstallPath = profile.InstallPath,
                    Processes = [new RuntimeHandoffProcessRecord
                    {
                        ProcessId = fake.Id,
                        ProcessName = "PalServer",
                        ExecutablePath = profile.ExecutablePath,
                        StartTimeUtc = DateTime.UtcNow.AddHours(-3)
                    }]
                };

                var outcome = await processes.ReconcileAsync(profile, badHint);
                True(outcome == ReconcileOutcome.Attached, "a real running managed process must still be found via the bounded path scan even when the handoff hint fails to verify");
            }
            finally { SyntheticPalServerHarness.TryKill(fake); }
        });
    }

    public static async Task TestReconcileReportsExitedDuringGapWhenHandoffExpectedButNothingIsRunning()
    {
        await WithSyntheticEnvironment(async (paths, logger, processes) =>
        {
            var profile = NewProfile(paths, "Gap Exit Test");
            // Nothing is actually running for this profile; the hint references a PID that will not resolve to it.
            var hint = new RuntimeHandoffServerRecord
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                InstallPath = profile.InstallPath,
                Processes = [new RuntimeHandoffProcessRecord
                {
                    ProcessId = int.MaxValue,
                    ProcessName = "PalServer",
                    ExecutablePath = profile.ExecutablePath,
                    StartTimeUtc = DateTime.UtcNow
                }]
            };

            var outcome = await processes.ReconcileAsync(profile, hint);
            Equal(ReconcileOutcome.ExitedDuringGap, outcome);
            True(processes.GetStatusText(profile).Contains("exit code unavailable", StringComparison.OrdinalIgnoreCase), "a gap-exit must be represented honestly rather than as a plain Stopped state");
        });
    }

    public static async Task TestReconcileReturnsNotRunningWhenNothingMatches()
    {
        await WithSyntheticEnvironment(async (paths, logger, processes) =>
        {
            var profile = NewProfile(paths, "Never Started Test");
            var outcome = await processes.ReconcileAsync(profile, hint: null);
            Equal(ReconcileOutcome.NotRunning, outcome);
            Equal("Stopped", processes.GetStatusText(profile));
        });
    }

    public static async Task TestReconcileDoesNotCrossAttachDifferentManagedProfiles()
    {
        await WithSyntheticEnvironment(async (paths, logger, processes) =>
        {
            var profileA = NewProfile(paths, "Profile A");
            var profileB = NewProfile(paths, "Profile B");
            using var fakeA = SyntheticPalServerHarness.Start(profileA.InstallPath, waitSeconds: 2, exitCode: 11);
            using var fakeB = SyntheticPalServerHarness.Start(profileB.InstallPath, waitSeconds: 2, exitCode: 22);
            try
            {
                var results = new Dictionary<Guid, ServerProcessLifetimeEndedEventArgs>();
                var bothDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                processes.ServerLifetimeEnded += (_, e) =>
                {
                    lock (results)
                    {
                        results[e.ServerId] = e;
                        if (results.Count == 2) bothDone.TrySetResult();
                    }
                };

                Equal(ReconcileOutcome.Attached, await processes.ReconcileAsync(profileA, hint: null));
                Equal(ReconcileOutcome.Attached, await processes.ReconcileAsync(profileB, hint: null));

                await bothDone.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Equal(11, results[profileA.Id].PrimaryExitCode);
                Equal(22, results[profileB.Id].PrimaryExitCode);
            }
            finally { SyntheticPalServerHarness.TryKill(fakeA); SyntheticPalServerHarness.TryKill(fakeB); }
        });
    }

    // ---- Full restart handoff: old Manager's ServerProcessService instance -> new one --------

    public static async Task TestFullRestartHandoffCycleReattachesAndCapturesExitCode()
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var registry = new ProfileRegistry(paths, logger);
            var settings = new PalworldSettingsService(logger);
            var rest = new PalworldRestClient(logger);
            var handoffService = new RuntimeHandoffService(paths, logger);

            var profile = new ServerProfile { Name = "Full Restart Handoff Test", InstallPath = Path.Combine(paths.ServersRoot, Guid.NewGuid().ToString("N"), "PalServer") };
            await registry.AddAsync(profile);

            using var fake = SyntheticPalServerHarness.Start(profile.InstallPath, waitSeconds: 3, exitCode: 42);
            try
            {
                // Old Manager, about to exit for an update: build and persist the handoff exactly
                // as ApplicationUpdateService.BuildHandoff/WriteAsync would, using its own
                // ServerProcessService instance's read-only handoff builder.
                var oldProcesses = new ServerProcessService(settings, rest, logger);
                var record = oldProcesses.BuildHandoffRecord(profile);
                True(record is not null, "the running synthetic process must produce a handoff record");
                await handoffService.WriteAsync(new RuntimeHandoffDocument
                {
                    OldManagerVersion = "0.3.0",
                    TargetManagerVersion = "0.3.1",
                    Servers = [record!]
                });
                // The old Manager's ServerProcessService instance is now discarded/released, as
                // it would be when the old Manager process exits.

                // New Manager starts: a brand new ServerProcessService with no prior in-memory
                // state, consuming the handoff exactly as App.OnStartup does.
                var newProcesses = new ServerProcessService(settings, rest, logger);
                var consumedHandoff = await handoffService.ConsumeAsync();
                True(consumedHandoff is not null, "the handoff must be consumable by the new Manager");
                var hint = consumedHandoff!.Servers.FirstOrDefault(s => s.ProfileId == profile.Id);
                True(hint is not null, "the consumed handoff must contain this profile");

                var outcome = await newProcesses.ReconcileAsync(profile, hint);
                Equal(ReconcileOutcome.Attached, outcome);
                Equal("Running (monitored)", newProcesses.GetStatusText(profile));
                True(newProcesses.IsOwnedLifetimeActive(profile), "the new Manager must own a monitored lifetime for the reattached server");

                var ended = new TaskCompletionSource<ServerProcessLifetimeEndedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                newProcesses.ServerLifetimeEnded += (_, e) => { if (e.ServerId == profile.Id) ended.TrySetResult(e); };
                var result = await ended.Task.WaitAsync(TimeSpan.FromSeconds(20));

                Equal(42, result.PrimaryExitCode);
                True(result.HasNonZeroExitCode, "exit code 42 must be classified as non-zero");
                Equal("Stopped / Error (exit 42)", newProcesses.GetStatusText(profile));
            }
            finally { SyntheticPalServerHarness.TryKill(fake); }
        });
    }

    // ---- helpers ---------------------------------------------------------------------

    private static ServerProfile NewProfile(AppPaths paths, string name)
    {
        var installPath = Path.Combine(paths.ServersRoot, Guid.NewGuid().ToString("N"), "PalServer");
        Directory.CreateDirectory(installPath);
        return new ServerProfile { Name = name, InstallPath = installPath };
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

    private static async Task WithSyntheticEnvironment(Func<AppPaths, IAppLogger, ServerProcessService, Task> body)
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var settings = new PalworldSettingsService(logger);
            var rest = new PalworldRestClient(logger);
            var processes = new ServerProcessService(settings, rest, logger);
            await body(paths, logger, processes);
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
}
