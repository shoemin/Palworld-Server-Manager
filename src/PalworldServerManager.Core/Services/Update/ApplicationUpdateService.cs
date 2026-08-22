using System.Text.Json;
using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;
using PalworldServerManager.Core.Services;

namespace PalworldServerManager.Core.Services.Update;

/// <summary>
/// Coordinates checking for, downloading, and applying a Manager update. Check/download never
/// touch Palworld or write a runtime handoff. Applying does, but only via this service's own
/// orchestration: it never calls ServerProcessService.StartAsync/StopAsync (only the read-only
/// ServerProcessService.BuildHandoffRecord) and never talks to Palworld's REST API directly -
/// this service has no PalworldRestClient dependency at all, structurally, so it cannot save,
/// shut down, or otherwise interact with a running Palworld server no matter what apply does.
/// </summary>
public sealed class ApplicationUpdateService
{
    private readonly IApplicationUpdateBackend _backend;
    private readonly IAppLogger _logger;
    private readonly ICriticalOperationTracker _operations;
    private readonly ProfileRegistry _registry;
    private readonly ServerProcessService _processes;
    private readonly RuntimeHandoffService _handoff;
    private readonly string _preferencesFile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private UpdateState _state = UpdateState.Idle;
    private ReleaseInfo? _availableRelease;
    private string? _errorMessage;
    private DateTime? _lastCheckedUtc;
    private int _downloadPercent;
    private UpdateChannel _channel;

    public ApplicationUpdateService(
        IApplicationUpdateBackend backend,
        AppPaths paths,
        IAppLogger logger,
        ICriticalOperationTracker operations,
        ProfileRegistry registry,
        ServerProcessService processes,
        RuntimeHandoffService handoff)
    {
        _backend = backend;
        _logger = logger;
        _operations = operations;
        _registry = registry;
        _processes = processes;
        _handoff = handoff;
        _preferencesFile = Path.Combine(paths.Root, "update-preferences.json");
        (_channel, _lastCheckedUtc) = LoadPreferences();

        // GetApplyBlockReason depends on ICriticalOperationTracker state that can change for
        // reasons entirely outside this service (e.g. an unrelated LAN transfer finishing). This
        // service and the tracker are both app-lifetime singletons constructed together, so this
        // subscription lives exactly as long as both and is never a leak; UI listeners (e.g.
        // UpdatesWindow) only ever subscribe to this service's own StatusChanged, unaffected.
        _operations.Changed += OnOperationsChanged;
    }

    private void OnOperationsChanged(object? sender, EventArgs e) => RaiseStatusChanged();

    /// <summary>
    /// Invoked immediately before the process exits to apply an update, so the App layer can
    /// stop Manager-only background services (the LAN Kestrel host and UDP discovery) that
    /// cannot survive process exit cleanly. Must never touch Palworld. A failure here is logged
    /// and otherwise ignored - it cannot corrupt state and must not block the update.
    /// </summary>
    public Func<CancellationToken, Task>? PreRestartShutdownAsync { get; set; }

    /// <summary>Invoked if an apply attempt fails after PreRestartShutdownAsync already ran, so those services can be resumed rather than left down for a version that never actually updates.</summary>
    public Func<CancellationToken, Task>? PostFailureRecoveryAsync { get; set; }

    public event EventHandler? StatusChanged;

    public UpdateStatus Status
    {
        get
        {
            lock (_sync)
                return new UpdateStatus(_state, _backend.ExecutionMode, _channel, _backend.CurrentVersion, _lastCheckedUtc, _availableRelease, _downloadPercent, _errorMessage);
        }
    }

    /// <summary>Changes the update channel. Any cached availability from the previous channel is discarded rather than shown/actioned against the new one.</summary>
    public void SetChannel(UpdateChannel channel)
    {
        bool changed;
        lock (_sync)
        {
            changed = _channel != channel;
            if (!changed) return;
            _channel = channel;
            if (_state is UpdateState.UpdateAvailable or UpdateState.ReadyToInstall or UpdateState.Failed)
            {
                _state = UpdateState.Idle;
                _availableRelease = null;
                _errorMessage = null;
                _downloadPercent = 0;
            }
        }

        if (!changed) return;
        SavePreferences();
        _logger.Info($"Update channel changed to {channel}.");
        RaiseStatusChanged();
    }

    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (_backend.ExecutionMode != UpdateExecutionMode.Installed)
        {
            _logger.Info($"Update check skipped: execution mode is {_backend.ExecutionMode}, not Installed.");
            return;
        }

        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            _logger.Info("Update check requested while another update operation is already in progress; ignored.");
            return;
        }

        try
        {
            UpdateChannel channel;
            lock (_sync) channel = _channel;

            SetState(UpdateState.Checking);
            _logger.Info($"Checking for updates. Channel={channel} ExecutionMode={_backend.ExecutionMode}.");

            var result = await _backend.CheckForUpdatesAsync(channel, cancellationToken);

            lock (_sync)
            {
                _lastCheckedUtc = DateTime.UtcNow;
                _availableRelease = result.UpdateAvailable ? result.Release : null;
                _errorMessage = null;
            }
            SavePreferences();

            if (result.UpdateAvailable && result.Release is not null)
            {
                _logger.Info($"Update available: {result.Release.Version}.");
                SetState(UpdateState.UpdateAvailable);
            }
            else
            {
                _logger.Info("No update available.");
                SetState(UpdateState.Idle);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Update check canceled.");
            SetState(UpdateState.Idle);
        }
        catch (Exception ex)
        {
            _logger.Error("Update check failed.", ex);
            lock (_sync) _errorMessage = "Could not check for updates: " + ex.Message;
            SetState(UpdateState.Failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DownloadUpdateAsync(CancellationToken cancellationToken = default)
    {
        ReleaseInfo? release;
        UpdateState currentState;
        lock (_sync) { release = _availableRelease; currentState = _state; }

        if (release is null || currentState != UpdateState.UpdateAvailable)
        {
            _logger.Warning("Download requested with no update currently staged as available; ignored.");
            return;
        }

        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            _logger.Info("Download requested while another update operation is already in progress; ignored.");
            return;
        }

        try
        {
            SetState(UpdateState.Downloading);
            lock (_sync) _downloadPercent = 0;
            RaiseStatusChanged();
            _logger.Info($"Downloading update {release.Version}.");

            var progress = new Progress<int>(percent =>
            {
                lock (_sync) _downloadPercent = Math.Clamp(percent, 0, 100);
                RaiseStatusChanged();
            });

            await _backend.DownloadUpdatesAsync(release, progress, cancellationToken);

            _logger.Info($"Update {release.Version} downloaded and ready to install.");
            SetState(UpdateState.ReadyToInstall);
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Update download canceled.");
            SetState(UpdateState.UpdateAvailable);
        }
        catch (Exception ex)
        {
            _logger.Error("Update download failed.", ex);
            lock (_sync) _errorMessage = "Could not download the update: " + ex.Message;
            SetState(UpdateState.Failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Non-committing check for UI purposes (button enable state / an explanatory message).
    /// Returns null when apply currently looks possible. This is advisory only - the real,
    /// race-safe check happens atomically inside ApplyAndRestartAsync via
    /// ICriticalOperationTracker.TryBeginShutdown, since a critical operation could start in the
    /// gap between this call returning and the user clicking Install and Restart.
    /// </summary>
    public string? GetApplyBlockReason()
    {
        UpdateState state;
        lock (_sync) state = _state;
        if (state != UpdateState.ReadyToInstall)
            return "No update is currently downloaded and ready to install.";
        if (_operations.IsBusy)
            return "Palworld Server Manager is busy: " + string.Join(", ", _operations.ActiveOperations) + ". Finish or cancel this before installing the Manager update.";
        return null;
    }

    /// <summary>
    /// Applies the downloaded update and restarts the Manager. A running Palworld server is
    /// never affected: this writes a runtime handoff describing whatever managed servers are
    /// currently running (so the restarted Manager can reattach), asks the App layer to stop
    /// Manager-only background services, then hands off to the external Velopack updater and
    /// returns - the caller is responsible for exiting the process immediately afterward on
    /// success. Blocked by any active critical Manager operation (never by a running server
    /// alone), and the block is enforced atomically so a new operation cannot slip in between
    /// the check and the actual shutdown commitment.
    /// </summary>
    public async Task<OperationResult> ApplyAndRestartAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
            return OperationResult.Fail("Another update operation is already in progress.");

        // Single rollback boundary: every stage that mutates external state (the shutdown gate,
        // the handoff file, Manager-only background services) sets its flag only once that stage
        // has actually happened, so a failure anywhere below - cancellation, a profile-load
        // error, a handoff-write error, an unexpected exception - is caught by the one catch
        // block below and unwound in RollBackAsync using exactly what these flags say really
        // happened. updaterLaunched=true is the commit point: once the external Velopack updater
        // has been handed the release, nothing above is ever undone, and Palworld is never part
        // of either the rollback or the commit path.
        var shutdownGateAcquired = false;
        var handoffWritten = false;
        var managerServicesStopped = false;
        var updaterLaunched = false;

        try
        {
            ReleaseInfo? release;
            UpdateState state;
            lock (_sync) { release = _availableRelease; state = _state; }

            if (release is null || state != UpdateState.ReadyToInstall)
                return OperationResult.Fail("No update is currently downloaded and ready to install.");

            if (!_operations.TryBeginShutdown(out var blockReason))
            {
                _logger.Warning($"Update apply blocked: {blockReason}");
                return OperationResult.Fail(blockReason ?? "Palworld Server Manager is busy with another operation.");
            }
            shutdownGateAcquired = true;

            SetState(UpdateState.Applying);
            _logger.Info($"Beginning update apply for version {release.Version}.");

            var profiles = await _registry.LoadAsync(cancellationToken);
            var handoffDocument = BuildHandoff(profiles, release.Version);

            await _handoff.WriteAsync(handoffDocument, cancellationToken);
            handoffWritten = true;

            if (PreRestartShutdownAsync is not null)
            {
                try
                {
                    await PreRestartShutdownAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    // Non-fatal by design: a LAN host that doesn't stop cleanly cannot corrupt
                    // state and cannot affect Palworld, and it will tear down with the process
                    // moments later regardless. Blocking the update on this would be worse. It was
                    // still attempted, though, so rollback must still try to resume it below.
                    _logger.Warning($"A Manager-only background service did not stop cleanly before restart; continuing anyway. {ex.Message}");
                }
                managerServicesStopped = true;
            }

            _backend.BeginApplyAndRestart(release);
            updaterLaunched = true; // Commit point: the external updater now owns this restart.

            _logger.Info($"Handed off to the external updater for version {release.Version}. Palworld Server Manager will exit shortly to complete the update.");
            _operations.CommitShutdown();
            return OperationResult.Ok("Update handed off to the installer. Palworld Server Manager will now close and reopen.");
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Update apply canceled; rolling back.");
            await RollBackAsync("The update apply was canceled.");
            return OperationResult.Fail("The update apply was canceled.");
        }
        catch (Exception ex)
        {
            _logger.Error("Update apply failed before the external updater was launched; rolling back. Palworld Server Manager remains on the current version and Palworld is unaffected.", ex);
            await RollBackAsync(ex.Message);
            return OperationResult.Fail("Could not apply the update: " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }

        async Task RollBackAsync(string reason)
        {
            if (updaterLaunched) return; // Past the commit point - nothing here is ever undone.

            if (managerServicesStopped && PostFailureRecoveryAsync is not null)
            {
                try { await PostFailureRecoveryAsync(CancellationToken.None); }
                catch (Exception recoveryEx) { _logger.Warning($"Could not resume Manager-only services after a failed update apply: {recoveryEx.Message}"); }
            }

            if (handoffWritten)
                await _handoff.DeleteAsync();

            if (shutdownGateAcquired)
                _operations.CancelShutdown();

            lock (_sync) _errorMessage = "Could not apply the update: " + reason;
            SetState(UpdateState.ReadyToInstall);
        }
    }

    private RuntimeHandoffDocument BuildHandoff(IReadOnlyList<ServerProfile> profiles, string targetVersion)
    {
        var servers = new List<RuntimeHandoffServerRecord>();
        foreach (var profile in profiles)
        {
            var record = _processes.BuildHandoffRecord(profile);
            if (record is not null) servers.Add(record);
        }

        return new RuntimeHandoffDocument
        {
            OldManagerVersion = _backend.CurrentVersion,
            TargetManagerVersion = targetVersion,
            Servers = servers
        };
    }

    private void SetState(UpdateState state)
    {
        lock (_sync) _state = state;
        RaiseStatusChanged();
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private (UpdateChannel Channel, DateTime? LastCheckedUtc) LoadPreferences()
    {
        try
        {
            // No preference has ever been explicitly saved: pick a sensible initial channel from
            // the package actually installed (e.g. a v0.4.0-alpha.1 install built for win-beta
            // should default to Prerelease, not silently start checking the wrong channel and
            // never surface its own subsequent alpha/beta updates) rather than hardcoding Stable.
            // Once a preference IS saved (below), it is always respected as-is from then on.
            if (!File.Exists(_preferencesFile)) return (InitialChannel(), null);
            var doc = JsonSerializer.Deserialize<UpdatePreferencesDocument>(File.ReadAllText(_preferencesFile), _json);
            if (doc is null) return (InitialChannel(), null);
            return (doc.Channel, doc.LastSuccessfulCheckUtc);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not read update preferences; defaulting to Stable. {ex.Message}");
            return (UpdateChannel.Stable, null);
        }
    }

    private UpdateChannel InitialChannel()
    {
        var channel = _backend.InstalledChannel ?? UpdateChannel.Stable;
        _logger.Info($"No saved update-channel preference found; defaulting to {channel} based on the installed package.");
        return channel;
    }

    private void SavePreferences()
    {
        try
        {
            UpdateChannel channel;
            DateTime? lastChecked;
            lock (_sync) { channel = _channel; lastChecked = _lastCheckedUtc; }

            var doc = new UpdatePreferencesDocument { Channel = channel, LastSuccessfulCheckUtc = lastChecked };
            var temp = _preferencesFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(doc, _json));
            File.Move(temp, _preferencesFile, true);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not persist update preferences: {ex.Message}");
        }
    }

    private sealed class UpdatePreferencesDocument
    {
        public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;
        public DateTime? LastSuccessfulCheckUtc { get; set; }
    }
}
