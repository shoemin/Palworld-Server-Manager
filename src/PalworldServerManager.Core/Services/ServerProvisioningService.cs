using System.Security.Cryptography;
using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services;

public sealed class ServerProvisioningService
{
    private readonly AppPaths _paths;
    private readonly SteamCmdService _steamCmd;
    private readonly PalworldSettingsService _settings;
    private readonly ProfileRegistry _registry;
    private readonly IAppLogger _logger;

    public ServerProvisioningService(AppPaths paths, SteamCmdService steamCmd, PalworldSettingsService settings, ProfileRegistry registry, IAppLogger logger)
    {
        _paths = paths;
        _steamCmd = steamCmd;
        _settings = settings;
        _registry = registry;
        _logger = logger;
    }

    public async Task<ServerProfile> CreateAsync(string name, int gamePort, int restPort, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        _logger.Info($"New managed-server creation requested. Name='{name}' GamePort={gamePort} RestPort={restPort}.");
        var id = Guid.NewGuid();
        var installPath = Path.Combine(_paths.ServersRoot, id.ToString("D"), "PalServer");
        var profile = new ServerProfile
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? "Palworld Server" : name.Trim(),
            InstallPath = installPath,
            GamePort = gamePort,
            RestApiPort = restPort,
            CreatedUtc = DateTime.UtcNow
        };

        try
        {
            await _steamCmd.InstallOrUpdatePalworldAsync(installPath, progress, cancellationToken);
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(18));
            _settings.ConfigureManagerDefaults(profile, profile.Name, restPort, password);
            await _registry.AddAsync(profile, cancellationToken);
            _logger.Info($"Created managed server '{profile.Name}' at {profile.InstallPath}.");
            return profile;
        }
        catch (Exception ex)
        {
            _logger.Error($"Managed-server creation failed for '{profile.Name}'. Cleaning incomplete profile directory.", ex);
            TryDeleteProfileDirectory(profile);
            throw;
        }
    }

    public async Task UpdateAsync(ServerProfile profile, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        _logger.Info($"Server update/validation requested for '{profile.Name}'.");
        await _steamCmd.InstallOrUpdatePalworldAsync(profile.InstallPath, progress, cancellationToken);
        _logger.Info($"Server update/validation completed for '{profile.Name}'.");
    }

    private static void TryDeleteProfileDirectory(ServerProfile profile)
    {
        try
        {
            var root = Directory.GetParent(profile.InstallPath)?.FullName;
            if (root is not null && Directory.Exists(root)) Directory.Delete(root, true);
        }
        catch { }
    }
}
