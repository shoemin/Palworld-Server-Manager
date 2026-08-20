using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services.Update;

/// <summary>
/// Velopack's own locator only distinguishes "installed" and "portable" (its own managed
/// portable package type) - anything else, including both a developer build and the project's
/// current non-Velopack release ZIP, looks identical to Velopack ("neither"). This resolves
/// that remaining ambiguity using one deliberately narrow, verifiable check (a sibling .csproj
/// next to a bin/Debug or bin/Release output folder) rather than a loose path-string guess,
/// and defaults to Portable - not Development - whenever that check is inconclusive, since a
/// confused developer seeing installer guidance is harmless but a real user seeing a
/// "development build" message is not.
/// </summary>
public static class UpdateExecutionModeDetector
{
    public static UpdateExecutionMode Detect(bool velopackIsInstalled, bool velopackIsPortable, string baseDirectory)
    {
        if (velopackIsInstalled) return UpdateExecutionMode.Installed;
        if (velopackIsPortable) return UpdateExecutionMode.Portable;
        return LooksLikeDevelopmentBuild(baseDirectory) ? UpdateExecutionMode.Development : UpdateExecutionMode.Portable;
    }

    public static bool LooksLikeDevelopmentBuild(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory)) return false;
        var separator = Path.DirectorySeparatorChar;
        if (!baseDirectory.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase))
            return false;

        var dir = new DirectoryInfo(baseDirectory);
        for (var depth = 0; depth < 6 && dir is not null; depth++, dir = dir.Parent)
        {
            if (dir.Exists && dir.GetFiles("*.csproj").Length > 0)
                return true;
        }

        return false;
    }
}
