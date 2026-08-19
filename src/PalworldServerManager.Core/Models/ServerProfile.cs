namespace PalworldServerManager.Core.Models;

public sealed class ServerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Palworld Server";
    public string InstallPath { get; set; } = string.Empty;
    public int GamePort { get; set; } = 8211;
    public int RestApiPort { get; set; } = 8212;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? ImportedFrom { get; set; }
    public DateTime? ImportedUtc { get; set; }
    public string AdditionalLaunchArguments { get; set; } = string.Empty;

    public string ExecutablePath => Path.Combine(InstallPath, "PalServer.exe");
    public string SavedPath => Path.Combine(InstallPath, "Pal", "Saved");
    public string SettingsPath => Path.Combine(SavedPath, "Config", "WindowsServer", "PalWorldSettings.ini");
    public string DefaultSettingsPath => Path.Combine(InstallPath, "DefaultPalWorldSettings.ini");
    public string ModsPath => Path.Combine(InstallPath, "Mods");

    public override string ToString() => Name;
}
