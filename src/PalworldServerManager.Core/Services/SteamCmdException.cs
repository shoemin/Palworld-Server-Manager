namespace PalworldServerManager.Core.Services;

public sealed class SteamCmdException : InvalidOperationException
{
    public SteamCmdException(int exitCode)
        : base($"SteamCMD exited with code {exitCode}. See the manager log for details.")
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }

    public bool SuggestSteamClientRecovery => ExitCode == 7;
}
