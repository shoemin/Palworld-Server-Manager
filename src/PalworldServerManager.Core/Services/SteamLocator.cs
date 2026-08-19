using System.Text.RegularExpressions;
using Microsoft.Win32;
using PalworldServerManager.Core.Infrastructure;

namespace PalworldServerManager.Core.Services;

public sealed class SteamLocator
{
    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;

    public SteamLocator(AppPaths paths, IAppLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public IReadOnlyList<string> GetExpectedPalServerPaths()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var steamRoot in GetSteamRoots())
        {
            roots.Add(steamRoot);
            foreach (var library in ReadSteamLibraries(steamRoot)) roots.Add(library);
        }

        foreach (var steamCmdRoot in GetExpectedSteamCmdRoots()) roots.Add(steamCmdRoot);

        var candidates = roots
            .Where(Directory.Exists)
            .Select(root => Path.Combine(root, "steamapps", "common", "PalServer"))
            .ToList();

        // Manager-owned locations are a known bounded pattern too. This lets discovery
        // identify an orphaned managed directory if the registry file is ever lost.
        if (Directory.Exists(_paths.ServersRoot))
        {
            foreach (var profileRoot in Directory.EnumerateDirectories(_paths.ServersRoot))
                candidates.Add(Path.Combine(profileRoot, "PalServer"));
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> GetExpectedSteamCmdRoots()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            _paths.SteamCmdRoot,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "steamcmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamCMD")
        };

        if (OperatingSystem.IsWindows()) candidates.Add(@"C:\steamcmd");
        return candidates.ToList();
    }

    public string? FindSteamCmdExecutable()
    {
        foreach (var root in GetExpectedSteamCmdRoots())
        {
            var exe = Path.Combine(root, "steamcmd.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    public bool IsSteamClientRunning()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("steam");
            try { return processes.Length > 0; }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not determine whether the Steam desktop client is running: {ex.Message}");
            return false;
        }
    }

    public string? FindSteamClientExecutable()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var root in GetSteamRoots())
        {
            var exe = Path.Combine(root, "steam.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    private IEnumerable<string> GetSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            TryRegistryPath(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath", roots);
            TryRegistryPath(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", roots);
            TryRegistryPath(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath", roots);
        }

        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(pf86)) roots.Add(Path.Combine(pf86, "Steam"));
        if (!string.IsNullOrWhiteSpace(pf)) roots.Add(Path.Combine(pf, "Steam"));
        return roots.Where(Directory.Exists);
    }

    private static void TryRegistryPath(RegistryKey baseKey, string subKey, string valueName, HashSet<string> roots)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            using var key = baseKey.OpenSubKey(subKey);
            if (key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value))
                roots.Add(value.Replace('/', Path.DirectorySeparatorChar));
        }
        catch
        {
            // Registry discovery is best-effort; standard filesystem locations are checked as a fallback.
        }
    }

    private IEnumerable<string> ReadSteamLibraries(string steamRoot)
    {
        var file = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(file)) yield break;

        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception ex)
        {
            _logger.Error($"Could not read Steam library file: {file}", ex);
            yield break;
        }

        foreach (Match match in Regex.Matches(text, "\\\"path\\\"\\s*\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase))
        {
            var path = match.Groups["path"].Value.Replace("\\\\", "\\");
            if (Directory.Exists(path)) yield return path;
        }
    }
}
