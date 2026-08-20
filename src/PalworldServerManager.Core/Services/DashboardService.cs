using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services;

public sealed class DashboardService
{
    private readonly AppPaths _paths;
    private readonly PalworldSettingsService _settings;
    private readonly PalworldRestClient _rest;
    private readonly ServerProcessService _processes;
    private readonly IAppLogger _logger;

    public DashboardService(
        AppPaths paths,
        PalworldSettingsService settings,
        PalworldRestClient rest,
        ServerProcessService processes,
        IAppLogger logger)
    {
        _paths = paths;
        _settings = settings;
        _rest = rest;
        _processes = processes;
        _logger = logger;
    }

    public async Task<DashboardSnapshot> GetSnapshotAsync(ServerProfile profile, CancellationToken cancellationToken = default)
    {
        var snapshot = new DashboardSnapshot
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            SourceMachine = Environment.MachineName,
            IsRunning = _processes.IsRunning(profile),
            ManagerStatus = _processes.GetStatusText(profile),
            GamePort = profile.GamePort,
            RestPort = profile.RestApiPort,
            LastBackupUtc = FindLastBackupUtc(profile)
        };

        var restConfig = _settings.GetRestConfiguration(profile);
        snapshot.RestConfigured = restConfig.RestEnabled && !string.IsNullOrWhiteSpace(restConfig.AdminPassword);
        snapshot.RestPort = restConfig.RestPort;

        if (!snapshot.IsRunning)
        {
            snapshot.RestError = "Server is not running.";
            return snapshot;
        }

        if (!restConfig.RestEnabled)
        {
            snapshot.RestError = "Palworld REST API is disabled for this server.";
            return snapshot;
        }

        if (string.IsNullOrWhiteSpace(restConfig.AdminPassword))
        {
            snapshot.RestError = "Palworld AdminPassword is not configured.";
            return snapshot;
        }

        var errors = new List<string>();

        try { snapshot.Info = await _rest.GetServerInfoAsync(restConfig.RestPort, restConfig.AdminPassword, cancellationToken); }
        catch (Exception ex) { errors.Add("info: " + ex.Message); }

        try { snapshot.Metrics = await _rest.GetMetricsAsync(restConfig.RestPort, restConfig.AdminPassword, cancellationToken); }
        catch (Exception ex) { errors.Add("metrics: " + ex.Message); }

        try { snapshot.Players = (await _rest.GetPlayersAsync(restConfig.RestPort, restConfig.AdminPassword, cancellationToken)).ToList(); }
        catch (Exception ex) { errors.Add("players: " + ex.Message); }

        try { snapshot.Settings = (await _rest.GetSettingsAsync(restConfig.RestPort, restConfig.AdminPassword, cancellationToken)).ToList(); }
        catch (Exception ex) { errors.Add("settings: " + ex.Message); }

        snapshot.RestAvailable = snapshot.Info is not null || snapshot.Metrics is not null || snapshot.Players.Count > 0 || snapshot.Settings.Count > 0;
        snapshot.RestError = errors.Count == 0 ? null : string.Join(" | ", errors);

        _logger.Debug($"Dashboard snapshot for '{profile.Name}': running={snapshot.IsRunning} restAvailable={snapshot.RestAvailable} players={snapshot.Players.Count} settings={snapshot.Settings.Count} errors={errors.Count}");
        return snapshot;
    }

    private DateTime? FindLastBackupUtc(ServerProfile profile)
    {
        var dir = Path.Combine(_paths.BackupsRoot, profile.Id.ToString("D"));
        if (!Directory.Exists(dir)) return null;

        try
        {
            return Directory.EnumerateFiles(dir, "*.zip", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Select(info => (DateTime?)info.LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
