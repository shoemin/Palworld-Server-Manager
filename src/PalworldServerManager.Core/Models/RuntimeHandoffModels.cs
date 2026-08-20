namespace PalworldServerManager.Core.Models;

public sealed class RuntimeHandoffDocument
{
    public int FormatVersion { get; set; } = 1;
    public Guid HandoffId { get; set; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string OldManagerVersion { get; set; } = string.Empty;
    public string TargetManagerVersion { get; set; } = string.Empty;
    public List<RuntimeHandoffServerRecord> Servers { get; set; } = [];
}

public sealed class RuntimeHandoffServerRecord
{
    public Guid ProfileId { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public string InstallPath { get; set; } = string.Empty;
    public List<RuntimeHandoffProcessRecord> Processes { get; set; } = [];
}

public sealed class RuntimeHandoffProcessRecord
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string? ExecutablePath { get; set; }
    public DateTime? StartTimeUtc { get; set; }
}
