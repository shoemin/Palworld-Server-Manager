using System.Diagnostics;

namespace PalworldServerManager.Core.Services;

public static class ProcessInspection
{
    public static bool IsPalServerRunningFrom(string installPath)
    {
        var processes = FindPalServerProcesses(installPath);
        try { return processes.Count > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static List<Process> FindPalServerProcesses(string installPath)
    {
        var matches = new List<Process>();
        if (!OperatingSystem.IsWindows()) return matches;

        var root = Path.GetFullPath(installPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            var keep = false;
            try
            {
                // PalServer.exe may bootstrap an Unreal shipping child such as
                // PalServer-Win64-Shipping-Cmd.exe. Match the installation path,
                // not just a single process name.
                if (!process.ProcessName.StartsWith("PalServer", StringComparison.OrdinalIgnoreCase)) continue;
                var executable = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executable)) continue;
                var full = Path.GetFullPath(executable);
                keep = full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
                if (keep) matches.Add(process);
            }
            catch
            {
                // Access-denied/process-exited races are normal during inspection.
            }
            finally
            {
                if (!keep) process.Dispose();
            }
        }
        return matches;
    }
}
