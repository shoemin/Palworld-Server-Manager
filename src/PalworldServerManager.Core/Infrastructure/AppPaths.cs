namespace PalworldServerManager.Core.Infrastructure;

public sealed class AppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PalworldServerManager");
        ServersRoot = Path.Combine(Root, "servers");
        BackupsRoot = Path.Combine(Root, "backups");
        LogsRoot = Path.Combine(Root, "logs");
        SteamCmdRoot = Path.Combine(Root, "steamcmd");
        ProfilesFile = Path.Combine(Root, "servers.json");
    }

    public string Root { get; }
    public string ServersRoot { get; }
    public string BackupsRoot { get; }
    public string LogsRoot { get; }
    public string SteamCmdRoot { get; }
    public string ProfilesFile { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ServersRoot);
        Directory.CreateDirectory(BackupsRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(SteamCmdRoot);
    }
}
