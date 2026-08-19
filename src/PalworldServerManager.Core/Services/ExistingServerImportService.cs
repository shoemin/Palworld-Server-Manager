using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services;

public sealed class ExistingServerImportService
{
    private readonly AppPaths _paths;
    private readonly ServerDiscoveryService _discovery;
    private readonly ProfileRegistry _registry;
    private readonly SteamCmdService _steamCmd;
    private readonly IAppLogger _logger;

    public ExistingServerImportService(AppPaths paths, ServerDiscoveryService discovery, ProfileRegistry registry, SteamCmdService steamCmd, IAppLogger logger)
    {
        _paths = paths;
        _discovery = discovery;
        _registry = registry;
        _steamCmd = steamCmd;
        _logger = logger;
    }

    public async Task<ServerProfile> ImportAsync(string sourcePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        _logger.Info($"Existing-server import analysis started for source '{sourcePath}'.");
        var profiles = await _registry.LoadAsync(cancellationToken);
        var candidate = _discovery.Analyze(sourcePath, profiles);
        _logger.Info($"Import candidate: name='{candidate.DisplayName}' classification={candidate.Classification} hasSettings={candidate.HasSettings} hasSave={candidate.HasSaveData} hasMods={candidate.HasMods} running={candidate.IsRunning}.");
        if (candidate.IsAlreadyManaged) throw new InvalidOperationException("This server is already managed or has already been imported.");
        if (candidate.Classification is ExistingServerClassification.Invalid or ExistingServerClassification.PossibleServer)
            throw new InvalidOperationException("The selected directory does not contain a complete enough Palworld dedicated-server installation to import safely.");
        if (candidate.IsRunning) throw new InvalidOperationException("The existing server is running. Stop it before importing so the world can be copied consistently.");

        var sourceSaved = Path.Combine(candidate.Path, "Pal", "Saved");
        var sourceMods = Path.Combine(candidate.Path, "Mods");
        progress?.Report("Hashing source server before import...");
        var savedBefore = await DirectoryHashService.HashTreeAsync(sourceSaved, cancellationToken);
        var modsBefore = await DirectoryHashService.HashTreeAsync(sourceMods, cancellationToken);
        _logger.Info($"Pre-import source hash inventory completed: SavedFiles={savedBefore.Count} ModFiles={modsBefore.Count}.");

        var id = Guid.NewGuid();
        var destination = Path.Combine(_paths.ServersRoot, id.ToString("D"), "PalServer");
        var restPort = 8212;
        if (candidate.HasSettings)
        {
            try
            {
                var sourceConfig = PalworldConfigParser.Load(Path.Combine(sourceSaved, "Config", "WindowsServer", "PalWorldSettings.ini"));
                if (int.TryParse(PalworldConfigParser.Unquote(sourceConfig.Get("RESTAPIPort")), out var parsedPort)) restPort = parsedPort;
            }
            catch { }
        }

        var profile = new ServerProfile
        {
            Id = id,
            Name = candidate.DisplayName,
            InstallPath = destination,
            RestApiPort = restPort,
            ImportedFrom = candidate.Path,
            ImportedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow
        };

        try
        {
            progress?.Report("Installing a clean Palworld server runtime...");
            _logger.Info($"Creating isolated managed destination '{destination}'.");
            await _steamCmd.InstallOrUpdatePalworldAsync(destination, progress, cancellationToken);

            progress?.Report("Copying world, configuration, and save data...");
            FileCopyService.CopyDirectory(sourceSaved, profile.SavedPath, overwrite: true);
            _logger.Info($"Copied source Saved tree into managed profile. SourceFiles={savedBefore.Count}.");
            if (Directory.Exists(sourceMods))
            {
                progress?.Report("Copying server mod configuration/content...");
                FileCopyService.CopyDirectory(sourceMods, profile.ModsPath, overwrite: true);
                _logger.Info($"Copied source Mods tree into managed profile. SourceFiles={modsBefore.Count}.");
            }

            progress?.Report("Verifying the original server was not modified...");
            var savedAfter = await DirectoryHashService.HashTreeAsync(sourceSaved, cancellationToken);
            var modsAfter = await DirectoryHashService.HashTreeAsync(sourceMods, cancellationToken);
            if (!DirectoryHashService.Equivalent(savedBefore, savedAfter, out var savedDifference))
                throw new InvalidOperationException("Source Saved data changed during import: " + savedDifference);
            if (!DirectoryHashService.Equivalent(modsBefore, modsAfter, out var modsDifference))
                throw new InvalidOperationException("Source Mods data changed during import: " + modsDifference);
            _logger.Info("Post-import source hash verification PASS: original Saved and Mods trees are unchanged.");

            await _registry.AddAsync(profile, cancellationToken);
            _logger.Info($"Imported existing Palworld server from '{candidate.Path}' into '{profile.InstallPath}' without modifying source data.");
            return profile;
        }
        catch (Exception ex)
        {
            _logger.Error($"Existing-server import failed. Cleaning incomplete managed destination '{destination}'.", ex);
            try
            {
                var profileRoot = Directory.GetParent(destination)?.FullName;
                if (profileRoot is not null && Directory.Exists(profileRoot)) Directory.Delete(profileRoot, true);
            }
            catch { }
            throw;
        }
    }
}
