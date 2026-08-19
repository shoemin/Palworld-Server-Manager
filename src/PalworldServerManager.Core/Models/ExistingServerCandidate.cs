namespace PalworldServerManager.Core.Models;

public enum ExistingServerClassification
{
    ValidExistingServer,
    FreshServerInstall,
    PossibleServer,
    AlreadyManaged,
    Invalid
}

public sealed class ExistingServerCandidate
{
    public string Path { get; set; } = string.Empty;
    public string DisplayName { get; set; } = "Palworld Server";
    public ExistingServerClassification Classification { get; set; }
    public bool HasExecutable { get; set; }
    public bool HasSettings { get; set; }
    public bool HasSaveData { get; set; }
    public bool HasMods { get; set; }
    public bool IsRunning { get; set; }
    public bool IsAlreadyManaged { get; set; }
    public DateTime? LastModifiedUtc { get; set; }
    public string Notes { get; set; } = string.Empty;

    public string Summary => $"{Classification} | Save: {(HasSaveData ? "Yes" : "No")} | Config: {(HasSettings ? "Yes" : "No")} | Running: {(IsRunning ? "Yes" : "No")}";
}
