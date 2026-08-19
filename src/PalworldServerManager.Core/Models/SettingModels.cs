namespace PalworldServerManager.Core.Models;

public sealed record SettingDefinition(string Key, string Category, string Description);

public sealed class SettingEditorItem
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? DefaultValue { get; set; }
    public string Category { get; set; } = "Advanced";
    public string Description { get; set; } = string.Empty;
    public bool IsKnown { get; set; }
}
