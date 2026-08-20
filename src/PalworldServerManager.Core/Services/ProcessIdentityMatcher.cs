using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services;

/// <summary>A minimal, synthesizable snapshot of an OS process, kept separate from System.Diagnostics.Process so identity rules are unit-testable without a real process.</summary>
public readonly record struct ProcessDescriptor(int ProcessId, string ProcessName, string? ExecutablePath, DateTime? StartTimeUtc);

/// <summary>
/// Decides whether a candidate process is safely identifiable as the specific managed
/// PalServer instance a runtime-handoff hint described. A PID match alone is never
/// sufficient: Windows reuses process IDs, so start time and executable location must
/// also agree before the Manager reattaches its lifetime monitor to it.
/// </summary>
public static class ProcessIdentityMatcher
{
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    public static bool IsSafeIdentityMatch(ProcessDescriptor candidate, string expectedInstallPath, RuntimeHandoffProcessRecord hint)
    {
        if (candidate.ProcessId != hint.ProcessId) return false;
        if (!IsRecognizedPalServerProcessName(candidate.ProcessName)) return false;
        if (!BelongsToInstall(candidate.ExecutablePath, expectedInstallPath)) return false;

        if (hint.StartTimeUtc is { } expectedStart)
        {
            if (candidate.StartTimeUtc is not { } actualStart) return false;
            if ((actualStart - expectedStart).Duration() > StartTimeTolerance) return false;
        }

        return true;
    }

    public static bool IsRecognizedPalServerProcessName(string processName)
        => !string.IsNullOrWhiteSpace(processName)
           && processName.StartsWith("PalServer", StringComparison.OrdinalIgnoreCase);

    public static bool BelongsToInstall(string? executablePath, string expectedInstallPath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        var root = Path.GetFullPath(expectedInstallPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(executablePath);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
