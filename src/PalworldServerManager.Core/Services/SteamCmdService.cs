using System.Diagnostics;
using System.IO.Compression;
using PalworldServerManager.Core.Infrastructure;

namespace PalworldServerManager.Core.Services;

public sealed class SteamCmdService
{
    public const int PalworldDedicatedServerAppId = 2394010;
    private static readonly Uri SteamCmdZipUri = new("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip");

    private readonly AppPaths _paths;
    private readonly SteamLocator _locator;
    private readonly IAppLogger _logger;
    private readonly HttpClient _httpClient;

    public SteamCmdService(AppPaths paths, SteamLocator locator, IAppLogger logger, HttpClient? httpClient = null)
    {
        _paths = paths;
        _locator = locator;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<string> EnsureInstalledAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var existing = _locator.FindSteamCmdExecutable();
        if (existing is not null)
        {
            _logger.Info($"Using existing SteamCMD at '{existing}'.");
            return existing;
        }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SteamCMD auto-install is implemented for Windows.");

        _paths.EnsureCreated();
        _logger.Info($"SteamCMD was not found in expected locations; downloading bootstrap archive into '{_paths.Root}'.");
        progress?.Report("Downloading SteamCMD...");
        var zip = Path.Combine(_paths.Root, "steamcmd.zip");
        await using (var response = await _httpClient.GetStreamAsync(SteamCmdZipUri, cancellationToken))
        await using (var output = File.Create(zip))
            await response.CopyToAsync(output, cancellationToken);

        progress?.Report("Extracting SteamCMD...");
        ZipFile.ExtractToDirectory(zip, _paths.SteamCmdRoot, overwriteFiles: true);
        File.Delete(zip);
        var exe = Path.Combine(_paths.SteamCmdRoot, "steamcmd.exe");
        if (!File.Exists(exe)) throw new InvalidOperationException("SteamCMD extraction completed but steamcmd.exe was not found.");
        _logger.Info($"SteamCMD installed at '{exe}'.");
        return exe;
    }

    public async Task InstallOrUpdatePalworldAsync(string destination, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destination);
        _logger.Info($"SteamCMD Palworld install/update requested. AppId={PalworldDedicatedServerAppId} Destination='{destination}'.");

        var steamRunning = _locator.IsSteamClientRunning();
        _logger.Info($"Steam desktop client detected before SteamCMD provisioning: {steamRunning}. Palworld provisioning uses anonymous SteamCMD authentication.");

        var steamCmd = await EnsureInstalledAsync(progress, cancellationToken);
        progress?.Report("Installing/updating Palworld Dedicated Server through SteamCMD...");

        var arguments = $"+force_install_dir \"{destination}\" +login anonymous +app_update {PalworldDedicatedServerAppId} validate +quit";
        var exitCode = await RunSteamCmdAsync(steamCmd, arguments, progress, cancellationToken);

        if (exitCode != 0)
            throw new SteamCmdException(exitCode);

        var exe = Path.Combine(destination, "PalServer.exe");
        if (!File.Exists(exe)) throw new InvalidOperationException("SteamCMD completed but PalServer.exe was not installed.");
        _logger.Info($"Palworld dedicated-server runtime verified at '{exe}'.");
    }

    private async Task<int> RunSteamCmdAsync(
        string steamCmd,
        string arguments,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = steamCmd,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(steamCmd)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                progress?.Report(e.Data);
                _logger.Info("SteamCMD: " + e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                progress?.Report(e.Data);
                _logger.Info("SteamCMD stderr: " + e.Data);
            }
        };

        if (!process.Start()) throw new InvalidOperationException("Could not start SteamCMD.");
        _logger.Info($"SteamCMD process started. PID={process.Id}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        _logger.Info($"SteamCMD process exited. PID={process.Id} ExitCode={process.ExitCode}.");
        return process.ExitCode;
    }
}
