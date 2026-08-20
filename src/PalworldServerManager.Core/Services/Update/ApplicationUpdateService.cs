using System.Text.Json;
using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services.Update;

/// <summary>
/// Coordinates checking for and downloading a Manager update. Deliberately stops at
/// ReadyToInstall - applying an update and restarting the Manager (runtime handoff, critical-
/// operation gating, LAN shutdown, process reattachment) is 4E and is not implemented here.
/// This service never references ServerProcessService or RuntimeHandoffService, so it is
/// structurally incapable of touching Palworld's process lifetime or writing handoff state.
/// </summary>
public sealed class ApplicationUpdateService
{
    private readonly IApplicationUpdateBackend _backend;
    private readonly IAppLogger _logger;
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

    public ApplicationUpdateService(IApplicationUpdateBackend backend, AppPaths paths, IAppLogger logger)
    {
        _backend = backend;
        _logger = logger;
        _preferencesFile = Path.Combine(paths.Root, "update-preferences.json");
        (_channel, _lastCheckedUtc) = LoadPreferences();
    }

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
            if (!File.Exists(_preferencesFile)) return (UpdateChannel.Stable, null);
            var doc = JsonSerializer.Deserialize<UpdatePreferencesDocument>(File.ReadAllText(_preferencesFile), _json);
            if (doc is null) return (UpdateChannel.Stable, null);
            return (doc.Channel, doc.LastSuccessfulCheckUtc);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not read update preferences; defaulting to Stable. {ex.Message}");
            return (UpdateChannel.Stable, null);
        }
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
