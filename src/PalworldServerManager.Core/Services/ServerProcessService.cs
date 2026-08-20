using System.Diagnostics;
using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services;

public sealed class ServerProcessService
{
    private readonly PalworldSettingsService _settings;
    private readonly PalworldRestClient _rest;
    private readonly IAppLogger _logger;
    private readonly object _lifetimeSync = new();
    private readonly Dictionary<Guid, TrackedServerLifetime> _trackedLifetimes = [];
    private readonly Dictionary<Guid, ServerProcessLifetimeEndedEventArgs> _lastLifetimeResults = [];
    private readonly HashSet<Guid> _expectedStops = [];
    private readonly SemaphoreSlim _startGate = new(1, 1);

    public ServerProcessService(PalworldSettingsService settings, PalworldRestClient rest, IAppLogger logger)
    {
        _settings = settings;
        _rest = rest;
        _logger = logger;
    }

    public event EventHandler<ServerProcessLifetimeEndedEventArgs>? ServerLifetimeEnded;

    /// <summary>
    /// True while a server process is physically present OR a process lifetime launched by
    /// this manager is still being finalized. The latter intentionally keeps Start locked
    /// until the manager has observed process termination and captured exit codes.
    /// </summary>
    public bool IsRunning(ServerProfile profile)
        => HasTrackedLifetime(profile.Id) || ProcessInspection.IsPalServerRunningFrom(profile.InstallPath);

    public bool IsOwnedLifetimeActive(ServerProfile profile) => HasTrackedLifetime(profile.Id);

    public string GetStatusText(ServerProfile profile)
    {
        if (HasTrackedLifetime(profile.Id))
            return ProcessInspection.IsPalServerRunningFrom(profile.InstallPath)
                ? "Running (monitored)"
                : "Exiting (capturing exit code)";
        if (ProcessInspection.IsPalServerRunningFrom(profile.InstallPath)) return "Running (external)";

        lock (_lifetimeSync)
        {
            if (!_lastLifetimeResults.TryGetValue(profile.Id, out var result)) return "Stopped";
            if (result.ExitCodeUnavailable) return "Stopped (exit code unavailable — Manager was restarting)";
            if (result.ExpectedStop) return "Stopped";
            if (result.HasNonZeroExitCode)
                return result.PrimaryExitCode is int code ? $"Stopped / Error (exit {code})" : "Stopped / Error";
            return result.PrimaryExitCode is int cleanCode ? $"Stopped (exit {cleanCode})" : "Stopped";
        }
    }

    public async Task<OperationResult> StartAsync(ServerProfile profile, IReadOnlyCollection<ServerProfile> allProfiles, CancellationToken cancellationToken = default)
    {
        _logger.Info($"Start requested for '{profile.Name}' id={profile.Id:D} install='{profile.InstallPath}' gamePort={profile.GamePort}.");
        if (!OperatingSystem.IsWindows()) return OperationResult.Fail("Palworld server launching is supported on Windows only.");
        if (!File.Exists(profile.ExecutablePath)) return OperationResult.Fail("PalServer.exe was not found. Install/update this server first.");

        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (HasTrackedLifetime(profile.Id))
                return OperationResult.Ok("Server is already running/being monitored. Another Start is blocked until the current server process exits and its exit code is captured.");
            if (ProcessInspection.IsPalServerRunningFrom(profile.InstallPath))
                return OperationResult.Ok("Server is already running. Another Start is blocked until all PalServer processes for this profile exit.");

            var other = allProfiles.FirstOrDefault(p => p.Id != profile.Id && IsRunning(p));
            if (other is not null)
            {
                if (HasTrackedLifetime(other.Id) && !ProcessInspection.IsPalServerRunningFrom(other.InstallPath))
                    return OperationResult.Fail($"'{other.Name}' is finishing its process lifetime. Wait for its exit code to be captured before starting '{profile.Name}'.");

                _logger.Info($"Server switch requested. Running server '{other.Name}' must stop before '{profile.Name}' can start.");
                var stop = await StopAsync(other, force: false, cancellationToken);
                if (!stop.Success)
                    return OperationResult.Fail($"Could not switch servers because '{other.Name}' could not be stopped gracefully. {stop.Message}");
                if (HasTrackedLifetime(other.Id))
                    return OperationResult.Fail($"'{other.Name}' has exited, but the manager has not finished capturing its process exit code yet. Start '{profile.Name}' again after '{other.Name}' changes to Stopped.");
            }

            var args = $"-port={profile.GamePort}";
            if (!string.IsNullOrWhiteSpace(profile.AdditionalLaunchArguments))
                args += " " + profile.AdditionalLaunchArguments.Trim();

            var info = new ProcessStartInfo
            {
                FileName = profile.ExecutablePath,
                Arguments = args,
                WorkingDirectory = profile.InstallPath,
                UseShellExecute = true
            };

            try
            {
                var launcher = Process.Start(info);
                if (launcher is null) return OperationResult.Fail("Windows did not return a process handle for PalServer.exe.");

                var lifetime = new TrackedServerLifetime(profile, launcher);
                lock (_lifetimeSync)
                {
                    if (_trackedLifetimes.ContainsKey(profile.Id))
                    {
                        launcher.Dispose();
                        return OperationResult.Ok("Server launch is already being monitored.");
                    }
                    _trackedLifetimes[profile.Id] = lifetime;
                    _lastLifetimeResults.Remove(profile.Id);
                    _expectedStops.Remove(profile.Id);
                }

                _logger.Info($"Launch request issued for '{profile.Name}' from {profile.ExecutablePath} {SanitizeLaunchArguments(args)}. LauncherPID={launcher.Id}. Lifetime monitoring is active until all PalServer processes exit.");
                _ = MonitorLifetimeAsync(lifetime);

                // Catch truly immediate launch failures while keeping normal server lifetime monitoring in the background.
                var completed = await Task.WhenAny(lifetime.Completion.Task, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));
                if (completed == lifetime.Completion.Task)
                {
                    var ended = await lifetime.Completion.Task;
                    return OperationResult.Fail($"Server exited during startup. {ended.Message}");
                }

                LogDetectedProcesses(profile, "after launch/monitor attachment");
                return OperationResult.Ok("Server launched. The manager will block another Start until the server exits and its process exit code is captured.");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start server '{profile.Name}'.", ex);
                return OperationResult.Fail(ex.Message);
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<OperationResult> StopAsync(ServerProfile profile, bool force, CancellationToken cancellationToken = default)
    {
        _logger.Info($"Stop requested for '{profile.Name}' id={profile.Id:D} force={force}.");

        var physicallyRunning = ProcessInspection.IsPalServerRunningFrom(profile.InstallPath);
        if (!physicallyRunning)
        {
            if (HasTrackedLifetime(profile.Id))
                return OperationResult.Fail("The server processes are exiting and the manager is waiting to capture their final exit code. Try again after the status changes to Stopped.");
            return OperationResult.Ok("Server is already stopped.");
        }

        LogDetectedProcesses(profile, "before stop");
        var ownsLifetime = HasTrackedLifetime(profile.Id);
        if (ownsLifetime) MarkExpectedStop(profile.Id);

        if (force)
        {
            try
            {
                var processes = ProcessInspection.FindPalServerProcesses(profile.InstallPath);
                try
                {
                    // Kill children/shipping processes first, then any root launcher that remains.
                    foreach (var process in processes.OrderByDescending(p => SafePathLength(p)))
                    {
                        try
                        {
                            if (!process.HasExited) process.Kill(entireProcessTree: true);
                        }
                        catch { }
                    }
                }
                finally { foreach (var process in processes) process.Dispose(); }

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (ProcessInspection.IsPalServerRunningFrom(profile.InstallPath) && DateTime.UtcNow < deadline)
                    await Task.Delay(250, cancellationToken);

                if (ProcessInspection.IsPalServerRunningFrom(profile.InstallPath))
                {
                    ClearExpectedStop(profile.Id);
                    LogDetectedProcesses(profile, "after failed force stop");
                    return OperationResult.Fail("One or more PalServer processes could not be terminated.");
                }

                // If this manager owns the lifetime, allow its monitor to capture the exit codes.
                await WaitForOwnedLifetimeFinalizationAsync(profile.Id, TimeSpan.FromSeconds(5), cancellationToken);
                _logger.Info($"Force-stopped server '{profile.Name}'.");
                return OperationResult.Ok("Server force-stopped.");
            }
            catch (Exception ex)
            {
                ClearExpectedStop(profile.Id);
                _logger.Error($"Failed to force-stop server '{profile.Name}'.", ex);
                return OperationResult.Fail(ex.Message);
            }
        }

        var (restEnabled, restPort, adminPassword) = _settings.GetRestConfiguration(profile);
        _logger.Info($"Safe-stop REST prerequisites for '{profile.Name}': enabled={restEnabled} port={restPort} adminPasswordConfigured={!string.IsNullOrWhiteSpace(adminPassword)}.");
        if (!restEnabled || string.IsNullOrWhiteSpace(adminPassword))
        {
            ClearExpectedStop(profile.Id);
            return OperationResult.Fail("REST API is not enabled/configured, so a safe automated shutdown is unavailable. Enable RESTAPIEnabled and AdminPassword in Settings, or use Force Stop only after saving in-game.");
        }

        try
        {
            _logger.Info($"Requesting REST world save for '{profile.Name}'.");
            await _rest.SaveAsync(restPort, adminPassword, cancellationToken);
            _logger.Info($"World save request completed; requesting REST shutdown for '{profile.Name}'.");
            await _rest.ShutdownAsync(restPort, adminPassword, 1, "Server manager shutdown", cancellationToken);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (ProcessInspection.IsPalServerRunningFrom(profile.InstallPath) && DateTime.UtcNow < deadline)
                await Task.Delay(500, cancellationToken);

            if (ProcessInspection.IsPalServerRunningFrom(profile.InstallPath))
            {
                ClearExpectedStop(profile.Id);
                LogDetectedProcesses(profile, "after REST shutdown timeout");
                return OperationResult.Fail("The REST shutdown request was accepted, but one or more PalServer processes remained running after 30 seconds.");
            }

            await WaitForOwnedLifetimeFinalizationAsync(profile.Id, TimeSpan.FromSeconds(5), cancellationToken);
            _logger.Info($"Gracefully stopped server '{profile.Name}' through the Palworld REST API.");
            return OperationResult.Ok("Server saved and stopped gracefully.");
        }
        catch (Exception ex)
        {
            ClearExpectedStop(profile.Id);
            _logger.Error($"Graceful shutdown failed for '{profile.Name}'.", ex);
            return OperationResult.Fail("Graceful REST shutdown failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Looks for a managed PalServer installation that is already running (typically because
    /// the Manager just restarted, whether for a self-update or a normal relaunch) and, if
    /// found, reattaches the lifetime monitor to it so future exits are still captured. This
    /// is safe to call for every profile at startup: it never starts a new server process and
    /// never attaches to an install that doesn't already have a matching process running.
    /// </summary>
    public Task<ReconcileOutcome> ReconcileAsync(ServerProfile profile, RuntimeHandoffServerRecord? hint, CancellationToken cancellationToken = default)
    {
        if (HasTrackedLifetime(profile.Id)) return Task.FromResult(ReconcileOutcome.AlreadyTracked);

        var seed = TryMatchHandoffHint(profile, hint);

        if (seed is null && hint is not null && hint.Processes.Count > 0 && !ProcessInspection.IsPalServerRunningFrom(profile.InstallPath))
        {
            // The handoff specifically expected this server to still be running, but nothing
            // verifies and nothing matching is physically present. Represent that honestly
            // instead of fabricating a successful exit code.
            RecordGapExit(profile);
            _logger.Warning($"Runtime handoff expected '{profile.Name}' to still be running after the Manager restarted, but no matching process was found. Recording exit-code-unavailable.");
            return Task.FromResult(ReconcileOutcome.ExitedDuringGap);
        }

        if (seed is null)
        {
            var found = ProcessInspection.FindPalServerProcesses(profile.InstallPath);
            if (found.Count == 0) return Task.FromResult(ReconcileOutcome.NotRunning);
            seed = found[0];
            for (var i = 1; i < found.Count; i++) found[i].Dispose();
        }

        BeginReattachedLifetime(profile, seed);
        return Task.FromResult(ReconcileOutcome.Attached);
    }

    private Process? TryMatchHandoffHint(ServerProfile profile, RuntimeHandoffServerRecord? hint)
    {
        if (hint is null) return null;

        foreach (var candidate in hint.Processes)
        {
            Process process;
            try { process = Process.GetProcessById(candidate.ProcessId); }
            catch { continue; }

            var descriptor = ToDescriptor(process);
            if (descriptor is { } d && ProcessIdentityMatcher.IsSafeIdentityMatch(d, profile.InstallPath, candidate))
            {
                _logger.Info($"Runtime handoff verified process identity for '{profile.Name}': PID={candidate.ProcessId} Name={candidate.ProcessName}.");
                return process;
            }

            _logger.Warning($"Runtime handoff hint for '{profile.Name}' PID={candidate.ProcessId} did not verify (possible PID reuse or mismatch); ignoring this hint entry rather than trusting it.");
            process.Dispose();
        }

        return null;
    }

    private void BeginReattachedLifetime(ServerProfile profile, Process seed)
    {
        var lifetime = new TrackedServerLifetime(profile, seed);
        lock (_lifetimeSync)
        {
            if (_trackedLifetimes.ContainsKey(profile.Id))
            {
                seed.Dispose();
                return;
            }
            _trackedLifetimes[profile.Id] = lifetime;
            _lastLifetimeResults.Remove(profile.Id);
            _expectedStops.Remove(profile.Id);
        }

        _logger.Info($"Reattached lifetime monitor to already-running server '{profile.Name}' seed PID={seed.Id}. Monitoring resumes as if the Manager had launched it.");
        _ = MonitorLifetimeAsync(lifetime);
    }

    private void RecordGapExit(ServerProfile profile)
    {
        var result = new ServerProcessLifetimeEndedEventArgs
        {
            ServerId = profile.Id,
            ServerName = profile.Name,
            ExpectedStop = false,
            ProcessExits = [],
            ExitCodeUnavailable = true,
            Message = $"Server '{profile.Name}' exited while Palworld Server Manager was restarting; exit code unavailable."
        };
        lock (_lifetimeSync) _lastLifetimeResults[profile.Id] = result;
    }

    private static ProcessDescriptor? ToDescriptor(Process process)
    {
        try
        {
            if (process.HasExited) return null;
            string? path;
            try { path = process.MainModule?.FileName; } catch { path = null; }
            DateTime? startUtc;
            try { startUtc = process.StartTime.ToUniversalTime(); } catch { startUtc = null; }
            return new ProcessDescriptor(process.Id, SafeProcessName(process), path, startUtc);
        }
        catch { return null; }
    }

    private async Task MonitorLifetimeAsync(TrackedServerLifetime lifetime)
    {
        var tracked = new Dictionary<int, TrackedProcess>();
        var exits = new List<ServerProcessExitInfo>();
        DateTime? emptySinceUtc = null;

        try
        {
            AddTrackedProcess(tracked, lifetime.Launcher);

            while (true)
            {
                var detected = ProcessInspection.FindPalServerProcesses(lifetime.Profile.InstallPath);
                try
                {
                    foreach (var process in detected)
                    {
                        if (tracked.ContainsKey(process.Id)) continue;
                        try
                        {
                            var retained = Process.GetProcessById(process.Id);
                            AddTrackedProcess(tracked, retained);
                            _logger.Info($"Lifetime monitor attached to server process. Server='{lifetime.Profile.Name}' PID={retained.Id} Name={SafeProcessName(retained)}.");
                        }
                        catch { }
                    }
                }
                finally { foreach (var process in detected) process.Dispose(); }

                foreach (var item in tracked.Values)
                {
                    if (item.ExitCaptured) continue;
                    try
                    {
                        if (!item.Process.HasExited) continue;
                        item.ExitCaptured = true;
                        item.ExitCode = item.Process.ExitCode;
                        exits.Add(new ServerProcessExitInfo(item.Process.Id, item.Name, item.ExitCode.Value));
                        _logger.Info($"Observed server process exit. Server='{lifetime.Profile.Name}' PID={item.Process.Id} Name={item.Name} ExitCode={item.ExitCode.Value}.");
                    }
                    catch (InvalidOperationException) { }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Could not read exit state for server process PID={SafeProcessId(item.Process)} Name={item.Name}: {ex.Message}");
                    }
                }

                var physicalRunning = ProcessInspection.IsPalServerRunningFrom(lifetime.Profile.InstallPath);
                var anyTrackedAlive = tracked.Values.Any(x => !SafeHasExited(x.Process));
                if (!physicalRunning && !anyTrackedAlive)
                {
                    emptySinceUtc ??= DateTime.UtcNow;
                    // A short empty grace period prevents a launcher->shipping handoff gap from
                    // being mistaken for the end of the server lifetime.
                    if (DateTime.UtcNow - emptySinceUtc.Value >= TimeSpan.FromSeconds(2)) break;
                }
                else
                {
                    emptySinceUtc = null;
                }

                await Task.Delay(250);
            }

            // Final capture pass after the no-process grace window.
            foreach (var item in tracked.Values)
            {
                if (item.ExitCaptured) continue;
                try
                {
                    if (!item.Process.HasExited) continue;
                    item.ExitCaptured = true;
                    item.ExitCode = item.Process.ExitCode;
                    exits.Add(new ServerProcessExitInfo(item.Process.Id, item.Name, item.ExitCode.Value));
                    _logger.Info($"Observed final server process exit. Server='{lifetime.Profile.Name}' PID={item.Process.Id} Name={item.Name} ExitCode={item.ExitCode.Value}.");
                }
                catch { }
            }

            var expected = ConsumeExpectedStop(lifetime.Profile.Id);
            var message = BuildLifetimeMessage(lifetime.Profile, exits, expected);
            var result = new ServerProcessLifetimeEndedEventArgs
            {
                ServerId = lifetime.Profile.Id,
                ServerName = lifetime.Profile.Name,
                ExpectedStop = expected,
                ProcessExits = exits.ToArray(),
                Message = message
            };

            lock (_lifetimeSync)
            {
                _trackedLifetimes.Remove(lifetime.Profile.Id);
                _lastLifetimeResults[lifetime.Profile.Id] = result;
            }

            if (expected)
                _logger.Info(message);
            else if (result.HasNonZeroExitCode)
                _logger.Error(message);
            else
                _logger.Warning(message);

            lifetime.Completion.TrySetResult(result);
            try { ServerLifetimeEnded?.Invoke(this, result); }
            catch (Exception ex) { _logger.Error("A ServerLifetimeEnded event handler failed.", ex); }
        }
        catch (Exception ex)
        {
            lock (_lifetimeSync) _trackedLifetimes.Remove(lifetime.Profile.Id);
            ClearExpectedStop(lifetime.Profile.Id);
            _logger.Error($"Server lifetime monitor failed for '{lifetime.Profile.Name}'.", ex);

            var result = new ServerProcessLifetimeEndedEventArgs
            {
                ServerId = lifetime.Profile.Id,
                ServerName = lifetime.Profile.Name,
                ExpectedStop = false,
                ProcessExits = exits.ToArray(),
                Message = $"Server lifetime monitoring failed: {ex.Message}"
            };
            lifetime.Completion.TrySetResult(result);
            try { ServerLifetimeEnded?.Invoke(this, result); } catch { }
        }
        finally
        {
            foreach (var item in tracked.Values)
            {
                try { item.Process.Dispose(); } catch { }
            }
        }
    }

    private async Task WaitForOwnedLifetimeFinalizationAsync(Guid serverId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        TrackedServerLifetime? lifetime;
        lock (_lifetimeSync) _trackedLifetimes.TryGetValue(serverId, out lifetime);
        if (lifetime is null) return;

        try
        {
            await lifetime.Completion.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.Warning($"Server processes exited, but lifetime finalization for serverId={serverId:D} did not complete within {timeout.TotalSeconds:0}s.");
        }
    }

    private bool HasTrackedLifetime(Guid serverId)
    {
        lock (_lifetimeSync) return _trackedLifetimes.ContainsKey(serverId);
    }

    private void MarkExpectedStop(Guid serverId)
    {
        lock (_lifetimeSync) _expectedStops.Add(serverId);
    }

    private void ClearExpectedStop(Guid serverId)
    {
        lock (_lifetimeSync) _expectedStops.Remove(serverId);
    }

    private bool ConsumeExpectedStop(Guid serverId)
    {
        lock (_lifetimeSync) return _expectedStops.Remove(serverId);
    }

    private static void AddTrackedProcess(Dictionary<int, TrackedProcess> tracked, Process process)
    {
        if (tracked.ContainsKey(process.Id)) return;
        // A Process obtained via GetProcessById/GetProcesses (as opposed to one returned by
        // Process.Start on this object) throws InvalidOperationException from ExitCode after
        // exit unless EnableRaisingEvents associates full exit-tracking state first.
        try { process.EnableRaisingEvents = true; } catch { }
        tracked[process.Id] = new TrackedProcess(process, SafeProcessName(process));
    }

    private static string BuildLifetimeMessage(ServerProfile profile, IReadOnlyList<ServerProcessExitInfo> exits, bool expected)
    {
        var codes = exits.Count == 0
            ? "exit code unavailable"
            : string.Join(", ", exits.Select(x => $"{x.ProcessName}[PID {x.ProcessId}]={x.ExitCode}"));

        if (expected) return $"Server '{profile.Name}' stopped as requested. Process exit codes: {codes}.";
        if (exits.Any(x => x.ExitCode != 0)) return $"Server '{profile.Name}' exited unexpectedly/crashed. Process exit codes: {codes}.";
        return $"Server '{profile.Name}' exited outside the manager (for example, its server window was closed manually). Process exit codes: {codes}.";
    }

    private void LogDetectedProcesses(ServerProfile profile, string phase)
    {
        var processes = ProcessInspection.FindPalServerProcesses(profile.InstallPath);
        try
        {
            if (processes.Count == 0)
            {
                _logger.Debug($"No PalServer processes detected for '{profile.Name}' {phase}.");
                return;
            }

            foreach (var process in processes)
            {
                try
                {
                    string path;
                    try { path = process.MainModule?.FileName ?? "<unknown>"; } catch { path = "<access denied>"; }
                    _logger.Debug($"Detected PalServer process {phase}: PID={process.Id} Name={process.ProcessName} Path='{path}'.");
                }
                catch { }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static string SanitizeLaunchArguments(string arguments)
    {
        return System.Text.RegularExpressions.Regex.Replace(arguments,
            @"(?i)(password|passwd|token|secret|apikey|api_key)(\s*[=:]?\s*)(""[^""]*""|\S+)",
            "$1$2***REDACTED***");
    }

    private static int SafePathLength(Process process)
    {
        try { return process.MainModule?.FileName?.Length ?? 0; }
        catch { return 0; }
    }

    private static bool SafeHasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static string SafeProcessName(Process process)
    {
        try { return process.ProcessName; }
        catch { return "PalServer"; }
    }

    private static int SafeProcessId(Process process)
    {
        try { return process.Id; }
        catch { return -1; }
    }

    private sealed class TrackedServerLifetime
    {
        public TrackedServerLifetime(ServerProfile profile, Process launcher)
        {
            Profile = profile;
            Launcher = launcher;
        }

        public ServerProfile Profile { get; }
        public Process Launcher { get; }
        public TaskCompletionSource<ServerProcessLifetimeEndedEventArgs> Completion { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TrackedProcess
    {
        public TrackedProcess(Process process, string name)
        {
            Process = process;
            Name = name;
        }

        public Process Process { get; }
        public string Name { get; }
        public bool ExitCaptured { get; set; }
        public int? ExitCode { get; set; }
    }
}
