namespace PalworldServerManager.Core.Models;

public sealed record OperationResult(bool Success, string Message)
{
    public static OperationResult Ok(string message) => new(true, message);
    public static OperationResult Fail(string message) => new(false, message);
}

public sealed class PortableServerManifest
{
    public string Format { get; set; } = "PalworldServerManagerExport";
    public int FormatVersion { get; set; } = 1;
    public string ServerName { get; set; } = string.Empty;
    public DateTime ExportedUtc { get; set; } = DateTime.UtcNow;
    public string ManagerVersion { get; set; } = "0.1.0";
    public int GamePort { get; set; } = 8211;
    public int RestApiPort { get; set; } = 8212;
    public List<PortableFileHash> Files { get; set; } = [];
}

public sealed class PortableFileHash
{
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Length { get; set; }
}
