using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services;

public sealed class ServerDiscoveryService
{
    private readonly SteamLocator _locator;
    private readonly ProfileRegistry _registry;
    private readonly IAppLogger? _logger;

    public ServerDiscoveryService(SteamLocator locator, ProfileRegistry registry, IAppLogger? logger = null)
    {
        _locator = locator;
        _registry = registry;
        _logger = logger;
    }

    public async Task<List<ExistingServerCandidate>> ScanExpectedLocationsAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await _registry.LoadAsync(cancellationToken);
        var expected = _locator.GetExpectedPalServerPaths();
        _logger?.Info($"Scanning {expected.Count} bounded/expected Palworld server path(s). No recursive drive scan is performed.");
        var results = new List<ExistingServerCandidate>();
        foreach (var path in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger?.Debug($"Discovery candidate path: '{path}' exists={Directory.Exists(path)}");
            if (!Directory.Exists(path)) continue;
            results.Add(Analyze(path, profiles));
        }

        var final = results
            .GroupBy(x => ProfileRegistry.NormalizePath(x.Path), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.Classification)
            .ThenBy(x => x.DisplayName)
            .ToList();
        _logger?.Info($"Bounded discovery completed with {final.Count} candidate(s).");
        return final;
    }

    public ExistingServerCandidate Analyze(string path, IReadOnlyCollection<ServerProfile> profiles)
    {
        path = ProfileRegistry.NormalizePath(path);
        var exe = Path.Combine(path, "PalServer.exe");
        var saved = Path.Combine(path, "Pal", "Saved");
        var settings = Path.Combine(saved, "Config", "WindowsServer", "PalWorldSettings.ini");
        var saveGames = Path.Combine(saved, "SaveGames");
        var defaultConfig = Path.Combine(path, "DefaultPalWorldSettings.ini");
        var mods = Path.Combine(path, "Mods");

        var alreadyManaged = profiles.Any(p =>
            ProfileRegistry.SamePath(p.InstallPath, path) ||
            (!string.IsNullOrWhiteSpace(p.ImportedFrom) && ProfileRegistry.SamePath(p.ImportedFrom!, path)));

        var hasExe = File.Exists(exe);
        var hasSettings = File.Exists(settings);
        var hasSave = Directory.Exists(saveGames) && Directory.EnumerateFiles(saveGames, "*", SearchOption.AllDirectories).Any();
        var hasDefault = File.Exists(defaultConfig);

        var classification = ExistingServerClassification.Invalid;
        if (alreadyManaged) classification = ExistingServerClassification.AlreadyManaged;
        else if (hasExe && hasSettings && hasSave) classification = ExistingServerClassification.ValidExistingServer;
        else if (hasExe && (hasDefault || Directory.Exists(saved))) classification = ExistingServerClassification.FreshServerInstall;
        else if (hasExe || hasSettings || hasSave) classification = ExistingServerClassification.PossibleServer;

        var displayName = Path.GetFileName(path);
        if (hasSettings)
        {
            try
            {
                var doc = PalworldConfigParser.Load(settings);
                var configured = PalworldConfigParser.Unquote(doc.Get("ServerName"));
                if (!string.IsNullOrWhiteSpace(configured)) displayName = configured;
            }
            catch (Exception ex)
            {
                _logger?.Warning($"Could not parse candidate settings at '{settings}': {ex.Message}");
            }
        }

        DateTime? lastModified = null;
        try
        {
            if (Directory.Exists(saved))
            {
                var files = Directory.EnumerateFiles(saved, "*", SearchOption.AllDirectories).ToList();
                if (files.Count > 0) lastModified = files.Max(File.GetLastWriteTimeUtc);
            }
        }
        catch (Exception ex) { _logger?.Warning($"Could not inspect last-modified time for '{saved}': {ex.Message}"); }

        var candidate = new ExistingServerCandidate
        {
            Path = path,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Palworld Server" : displayName,
            Classification = classification,
            HasExecutable = hasExe,
            HasSettings = hasSettings,
            HasSaveData = hasSave,
            HasMods = Directory.Exists(mods),
            IsRunning = ProcessInspection.IsPalServerRunningFrom(path),
            IsAlreadyManaged = alreadyManaged,
            LastModifiedUtc = lastModified,
            Notes = classification switch
            {
                ExistingServerClassification.ValidExistingServer => "Ready to import as a managed copy.",
                ExistingServerClassification.FreshServerInstall => "Palworld server files were found, but no populated world save was detected.",
                ExistingServerClassification.PossibleServer => "Some expected Palworld files were found, but the installation is incomplete.",
                ExistingServerClassification.AlreadyManaged => "This installation or import source is already registered.",
                _ => "Does not match the expected Palworld dedicated-server layout."
            }
        };
        _logger?.Debug($"Analyzed candidate '{path}': classification={candidate.Classification} exe={hasExe} settings={hasSettings} save={hasSave} mods={candidate.HasMods} running={candidate.IsRunning} alreadyManaged={alreadyManaged}");
        return candidate;
    }
}
