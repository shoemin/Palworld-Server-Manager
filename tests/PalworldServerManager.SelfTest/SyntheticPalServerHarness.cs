using System.Diagnostics;

namespace PalworldServerManager.SelfTest;

/// <summary>
/// Starts a harmless, real Windows process that ProcessInspection's name/path matching will
/// recognize as a managed PalServer install, without needing an actual Palworld binary. It works
/// by copying this already-built self-test apphost to "PalServer.exe" and running it in
/// --harness mode (see Program.cs), which just sleeps then exits with a controlled code. The
/// apphost's embedded managed-assembly reference is a relative path resolved next to the exe and
/// is independent of the exe's own filename, so the companion .dll/.deps.json/.runtimeconfig.json
/// are copied unrenamed alongside it.
/// </summary>
internal static class SyntheticPalServerHarness
{
    /// <summary>Copies the synthetic PalServer.exe and its companions into installPath without launching it, for tests where production code (e.g. ServerProcessService.StartAsync) does the launching itself with its own real arguments.</summary>
    public static void CopyInto(string installPath)
    {
        Directory.CreateDirectory(installPath);
        var sourceDir = AppContext.BaseDirectory;
        const string sourceName = "PalworldServerManager.SelfTest";

        // Copy every managed assembly and runtime config alongside the apphost, not just this
        // one assembly's own files. The apphost JITs its entry method before running any of it,
        // which resolves every type that method references - so a dependency missing from this
        // directory fails the process at startup even when the harness branch itself would never
        // touch that dependency. (#40 added a Host.Persistence reference and surfaced exactly
        // that: the copied process died with 0xE0434352 before reaching the --harness branch.)
        foreach (var source in Directory.GetFiles(sourceDir, "*.dll"))
        {
            File.Copy(source, Path.Combine(installPath, Path.GetFileName(source)), true);
        }

        foreach (var extension in new[] { ".runtimeconfig.json", ".deps.json" })
        {
            var source = Path.Combine(sourceDir, sourceName + extension);
            if (File.Exists(source)) File.Copy(source, Path.Combine(installPath, sourceName + extension), true);
        }

        File.Copy(Path.Combine(sourceDir, sourceName + ".exe"), Path.Combine(installPath, "PalServer.exe"), true);
    }

    public static Process Start(string installPath, int waitSeconds, int exitCode)
    {
        CopyInto(installPath);
        var exePath = Path.Combine(installPath, "PalServer.exe");
        var info = new ProcessStartInfo
        {
            FileName = exePath,
            ArgumentList = { "--harness", waitSeconds.ToString(), exitCode.ToString() },
            WorkingDirectory = installPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return Process.Start(info) ?? throw new InvalidOperationException("Failed to start the synthetic PalServer.exe test process.");
    }

    public static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}
