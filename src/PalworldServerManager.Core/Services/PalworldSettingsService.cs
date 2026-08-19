using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services;

public sealed class PalworldSettingsService
{
    private readonly IAppLogger? _logger;

    public PalworldSettingsService(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public Task<List<SettingEditorItem>> LoadForEditingAsync(ServerProfile profile)
    {
        EnsureActiveConfig(profile);
        var active = PalworldConfigParser.Load(profile.SettingsPath);
        PalworldConfigDocument? defaults = File.Exists(profile.DefaultSettingsPath)
            ? PalworldConfigParser.Load(profile.DefaultSettingsPath)
            : null;

        var keys = new List<string>();
        foreach (var pair in active.Entries)
            if (!keys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)) keys.Add(pair.Key);
        if (defaults is not null)
            foreach (var pair in defaults.Entries)
                if (!keys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)) keys.Add(pair.Key);

        var items = keys.Select(key =>
        {
            var definition = PalworldSettingSchema.Find(key);
            return new SettingEditorItem
            {
                Key = key,
                Value = active.Get(key) ?? defaults?.Get(key) ?? string.Empty,
                DefaultValue = defaults?.Get(key),
                Category = definition?.Category ?? "Advanced / Unknown",
                Description = definition?.Description ?? "Setting discovered from this Palworld installation; preserved even when this manager does not recognize it.",
                IsKnown = definition is not null
            };
        }).OrderBy(x => x.Category).ThenBy(x => x.Key).ToList();

        _logger?.Info($"Loaded {items.Count} editable setting(s) for server '{profile.Name}'. Known={items.Count(x => x.IsKnown)} Unknown={items.Count(x => !x.IsKnown)}");
        return Task.FromResult(items);
    }

    public Task SaveAsync(ServerProfile profile, IEnumerable<SettingEditorItem> items)
    {
        EnsureActiveConfig(profile);
        var materialized = items.ToList();
        var doc = PalworldConfigParser.Load(profile.SettingsPath);
        foreach (var item in materialized)
            doc.Set(item.Key, item.Value.Trim());

        var directory = Path.GetDirectoryName(profile.SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temp = profile.SettingsPath + ".tmp";
        File.WriteAllText(temp, doc.Serialize());
        File.Move(temp, profile.SettingsPath, true);
        _logger?.Info($"Saved {materialized.Count} setting(s) for server '{profile.Name}'. Sensitive setting values are not written to manager logs.");
        return Task.CompletedTask;
    }

    public void EnsureActiveConfig(ServerProfile profile)
    {
        if (File.Exists(profile.SettingsPath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(profile.SettingsPath)!);
        if (File.Exists(profile.DefaultSettingsPath))
        {
            File.Copy(profile.DefaultSettingsPath, profile.SettingsPath, false);
            _logger?.Info($"Initialized active settings for '{profile.Name}' from DefaultPalWorldSettings.ini.");
        }
        else
        {
            File.WriteAllText(profile.SettingsPath, "[/Script/Pal.PalGameWorldSettings]" + Environment.NewLine + "OptionSettings=()" + Environment.NewLine);
            _logger?.Warning($"DefaultPalWorldSettings.ini was not found for '{profile.Name}'. Created an empty active settings document.");
        }
    }

    public (bool RestEnabled, int RestPort, string AdminPassword) GetRestConfiguration(ServerProfile profile)
    {
        if (!File.Exists(profile.SettingsPath)) return (false, profile.RestApiPort, string.Empty);
        var doc = PalworldConfigParser.Load(profile.SettingsPath);
        var enabled = bool.TryParse(PalworldConfigParser.Unquote(doc.Get("RESTAPIEnabled")), out var b) && b;
        var port = int.TryParse(PalworldConfigParser.Unquote(doc.Get("RESTAPIPort")), out var p) ? p : profile.RestApiPort;
        var password = PalworldConfigParser.Unquote(doc.Get("AdminPassword"));
        _logger?.Debug($"REST configuration for '{profile.Name}': enabled={enabled} port={port} adminPasswordConfigured={!string.IsNullOrWhiteSpace(password)}");
        return (enabled, port, password);
    }

    public void ConfigureManagerDefaults(ServerProfile profile, string serverName, int restPort, string adminPassword)
    {
        EnsureActiveConfig(profile);
        var doc = PalworldConfigParser.Load(profile.SettingsPath);
        doc.Set("ServerName", PalworldConfigParser.Quote(serverName));
        doc.Set("RESTAPIEnabled", "True");
        doc.Set("RESTAPIPort", restPort.ToString());
        doc.Set("AdminPassword", PalworldConfigParser.Quote(adminPassword));
        doc.Set("bIsUseBackupSaveData", "True");
        File.WriteAllText(profile.SettingsPath, doc.Serialize());
        _logger?.Info($"Configured manager defaults for '{profile.Name}': REST enabled on port {restPort}; backup-save enabled. Generated password value is not logged.");
    }
}
