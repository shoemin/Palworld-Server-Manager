using System.Diagnostics;
using System.Text.Json;

namespace PalworldServerManager.SelfTest;

// Structural dependency-direction guards for the v0.5 topology accepted by #19
// (docs/developer/v0.5-architecture.md SS1) and scaffolded by #39. These read each project's
// actual MSBuild-EVALUATED ProjectReference items (via `dotnet msbuild -getItem:ProjectReference`,
// not raw .csproj XML text) so a forbidden or missing reference fails the moment it's changed -
// it doesn't require the referencing project to actually build first, and it can't be defeated by
// a Condition, Remove/Update, Exclude, Directory.Build.props/targets import, explicit <Import>, or
// a Target-scoped item, the way a raw-XML text scan could (PR #58 review rounds 1-4).
public static class ArchitectureGuardTests
{
    private const string Core = "PalworldServerManager.Core";
    private const string Lan = "PalworldServerManager.Lan";
    private const string App = "PalworldServerManager.App";
    private const string Contracts = "PalworldServerManager.Contracts";
    private const string Host = "PalworldServerManager.Host";
    private const string HostPersistence = "PalworldServerManager.Host.Persistence";
    private const string HostCli = "PalworldServerManager.Host.Cli";
    private const string PlatformContracts = "PalworldServerManager.Platform.Contracts";
    private const string PlatformWindows = "PalworldServerManager.Platform.Windows";
    private const string PlatformLinux = "PalworldServerManager.Platform.Linux";
    private const string ClientCli = "PalworldServerManager.Client.Cli";
    private const string ClientAvalonia = "PalworldServerManager.Client.Avalonia";
    private const string ClientPlatformContracts = "PalworldServerManager.Client.Platform.Contracts";
    private const string ClientPlatformWindows = "PalworldServerManager.Client.Platform.Windows";
    private const string ClientPlatformLinux = "PalworldServerManager.Client.Platform.Linux";

    // Every project #39 is responsible for scaffolding. Host-side Platform.Linux and
    // Client.Platform.Linux are deliberately excluded from this project's own scope (#21).
    private static readonly string[] AllNewV05Projects =
    [
        Contracts, Host, HostPersistence, HostCli, PlatformContracts, PlatformWindows,
        ClientCli, ClientAvalonia, ClientPlatformContracts, ClientPlatformWindows
    ];

    // The exact, complete set of direct ProjectReference edges the accepted #19 SS1 topology
    // authorizes for the current Windows-stage graph - both an allowlist (no unexpected extra
    // edge survives) and a requirement (every listed edge must actually be present). This is
    // deliberately the single source of truth the transitive isolation checks below are built
    // on top of, not a separate, looser approximation of the same graph.
    private static readonly Dictionary<string, string[]> AllowedDirectReferences = new()
    {
        [Contracts] = [],
        [PlatformContracts] = [Core],
        [PlatformWindows] = [PlatformContracts, Core],
        [ClientPlatformContracts] = [],
        [ClientPlatformWindows] = [ClientPlatformContracts],
        [HostPersistence] = [Core],
        [Host] = [Core, Contracts, PlatformContracts, HostPersistence, PlatformWindows],
        [HostCli] = [Core, HostPersistence, PlatformWindows],
        [ClientCli] = [Contracts, ClientPlatformContracts, ClientPlatformWindows],
        [ClientAvalonia] = [Contracts, ClientPlatformContracts, ClientPlatformWindows],
    };

    public static Task TestDirectReferenceGraphMatchesAcceptedTopologyExactly()
    {
        foreach (var (project, allowed) in AllowedDirectReferences)
        {
            var actual = DirectReferences(project);
            var allowedSet = allowed.ToHashSet();
            var actualSet = actual.ToHashSet();

            var unexpected = actualSet.Except(allowedSet).ToList();
            if (unexpected.Count > 0)
            {
                throw new Exception($"{project} has unexpected direct ProjectReference(s) outside the accepted #19 SS1 topology: {string.Join(", ", unexpected)}. Allowed: {(allowed.Length == 0 ? "(none)" : string.Join(", ", allowed))}.");
            }

            var missing = allowedSet.Except(actualSet).ToList();
            if (missing.Count > 0)
            {
                throw new Exception($"{project} is missing required direct ProjectReference(s) from the accepted #19 SS1 topology: {string.Join(", ", missing)}.");
            }
        }

        return Task.CompletedTask;
    }

    public static Task TestContractsIsCoreIndependent()
    {
        NotDependsOn(Contracts, Core);
        return Task.CompletedTask;
    }

    public static Task TestContractsHasNoLanDependency()
    {
        NotDependsOn(Contracts, Lan);
        return Task.CompletedTask;
    }

    public static Task TestNoNewV05ProjectReferencesLegacyLan()
    {
        foreach (var project in AllNewV05Projects)
        {
            NotDependsOn(project, Lan);
        }

        return Task.CompletedTask;
    }

    public static Task TestClientAvaloniaHasNoHostSideDependencyPath()
    {
        AssertNoHostSideDependencyPath(ClientAvalonia);
        return Task.CompletedTask;
    }

    public static Task TestClientCliHasNoHostSideDependencyPath()
    {
        AssertNoHostSideDependencyPath(ClientCli);
        return Task.CompletedTask;
    }

    public static Task TestHostCliHasNoContractsReference()
    {
        // Host.Cli is privileged offline bootstrap/recovery only (CLIENT-003) - it never speaks
        // the ordinary online client protocol, so it must never reference Contracts at all, not
        // merely "not use it in a normal mode".
        NotDependsOn(HostCli, Contracts);
        return Task.CompletedTask;
    }

    public static Task TestOrdinaryClientsShareClientPlatformContracts()
    {
        DependsOn(ClientAvalonia, ClientPlatformContracts);
        DependsOn(ClientCli, ClientPlatformContracts);
        return Task.CompletedTask;
    }

    public static Task TestWindowsAndLinuxImplementationsDoNotReferenceEachOther()
    {
        // No Platform.Linux/Client.Platform.Linux project exists yet (#21, out of scope for
        // #39) - this asserts the Windows side never references a "*.Linux" sibling by name,
        // so the check is meaningful now and keeps failing correctly once Linux is added.
        AssertNoReferenceToProjectEndingWith(PlatformWindows, ".Linux");
        AssertNoReferenceToProjectEndingWith(ClientPlatformWindows, ".Linux");

        if (ProjectExists(PlatformLinux))
        {
            NotDependsOn(PlatformLinux, PlatformWindows);
        }

        if (ProjectExists(ClientPlatformLinux))
        {
            NotDependsOn(ClientPlatformLinux, ClientPlatformWindows);
        }

        return Task.CompletedTask;
    }

    public static Task TestCoreHasNoDependencyOnAnyNewV05Project()
    {
        // Core's accepted dependency set is BCL-only plus one explicit legacy Velopack
        // carve-out (ARCH-001) - it must never acquire a ProjectReference to anything this
        // issue scaffolds. Core itself is not modified by #39; this only asserts that fact.
        var actual = DirectReferences(Core).ToHashSet();
        var offenders = actual.Intersect(AllNewV05Projects).ToList();
        if (offenders.Count > 0)
        {
            throw new Exception($"Core has acquired a forbidden dependency on new v0.5 project(s): {string.Join(", ", offenders)}.");
        }

        return Task.CompletedTask;
    }

    public static Task TestFrozenWpfAppStillReferencesLanUnchanged()
    {
        // A transitive DependsOn(App, Lan) check alone would still pass if App gained a new
        // forbidden direct reference, or if the required direct App -> Lan edge were replaced by
        // an indirect path through an intermediate project - neither actually preserves "frozen"
        // (ARCH-001/ARCH-002). Assert App's direct reference set exactly instead.
        var actual = DirectReferences(App).ToHashSet();
        var expected = new HashSet<string> { Core, Lan };

        var unexpected = actual.Except(expected).ToList();
        if (unexpected.Count > 0)
        {
            throw new Exception($"{App} has unexpected direct ProjectReference(s) beyond its frozen {{Core, Lan}} set: {string.Join(", ", unexpected)}.");
        }

        var missing = expected.Except(actual).ToList();
        if (missing.Count > 0)
        {
            throw new Exception($"{App} is missing required direct ProjectReference(s) from its frozen {{Core, Lan}} set: {string.Join(", ", missing)}.");
        }

        return Task.CompletedTask;
    }

    private static void AssertNoHostSideDependencyPath(string clientProject)
    {
        NotDependsOn(clientProject, Core);
        NotDependsOn(clientProject, Host);
        NotDependsOn(clientProject, HostPersistence);
        NotDependsOn(clientProject, PlatformContracts);
        NotDependsOn(clientProject, PlatformWindows);
    }

    private static void DependsOn(string project, string target)
    {
        if (!TransitiveReferences(project).Contains(target))
        {
            throw new Exception($"{project} does not reference {target} (directly or transitively), but the accepted #19 topology requires it to.");
        }
    }

    private static void NotDependsOn(string project, string forbidden)
    {
        var references = TransitiveReferences(project);
        if (references.Contains(forbidden))
        {
            throw new Exception($"{project} has a forbidden dependency path to {forbidden}. References found: {string.Join(", ", references)}");
        }
    }

    private static void AssertNoReferenceToProjectEndingWith(string project, string suffix)
    {
        var offender = DirectReferences(project).FirstOrDefault(r => r.EndsWith(suffix, StringComparison.Ordinal));
        if (offender is not null)
        {
            throw new Exception($"{project} references {offender}, matching forbidden suffix '{suffix}'.");
        }
    }

    private static HashSet<string> TransitiveReferences(string project)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<string>(DirectReferences(project));
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (!visited.Add(next))
            {
                continue;
            }

            foreach (var transitive in DirectReferences(next))
            {
                queue.Enqueue(transitive);
            }
        }

        return visited;
    }

    // Evaluated once per project per self-test run and reused - each entry costs one
    // `dotnet msbuild` process launch, and the transitive guards call this repeatedly.
    private static readonly Dictionary<string, List<string>> DirectReferenceCache = new();

    private static List<string> DirectReferences(string project)
    {
        if (DirectReferenceCache.TryGetValue(project, out var cached))
        {
            return cached;
        }

        var references = EvaluateDirectReferences(project, ResolveCsprojPath(project));
        DirectReferenceCache[project] = references;
        return references;
    }

    // Asks MSBuild itself what project's ProjectReference items evaluate to, rather than
    // reimplementing MSBuild's Condition/Remove/Update/Exclude/Import/Target semantics in a raw
    // XML reader (PR #58 review rounds 1-4 each found a real gap in that reimplementation). No
    // target is requested, so this is pure evaluation - no build output, no side effects.
    private static List<string> EvaluateDirectReferences(string project, string csprojPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = RepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(csprojPath);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-getItem:ProjectReference");
        startInfo.ArgumentList.Add("-p:Configuration=Release");

        using var process = Process.Start(startInfo)
            ?? throw new Exception($"Failed to start 'dotnet msbuild' to evaluate {project}'s ProjectReference items.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new Exception($"'dotnet msbuild {csprojPath} -getItem:ProjectReference' exited {process.ExitCode} while evaluating {project} - this guard trusts only a clean MSBuild evaluation. stderr: {stderr}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stdout);
        }
        catch (JsonException ex)
        {
            throw new Exception($"'dotnet msbuild {csprojPath} -getItem:ProjectReference' did not return parseable JSON while evaluating {project}: {ex.Message}. Output: {stdout}");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("Items", out var items) || !items.TryGetProperty("ProjectReference", out var references))
            {
                throw new Exception($"'dotnet msbuild {csprojPath} -getItem:ProjectReference' output for {project} had no Items.ProjectReference array. Output: {stdout}");
            }

            var result = new List<string>();
            foreach (var reference in references.EnumerateArray())
            {
                if (!reference.TryGetProperty("FullPath", out var fullPathElement) || fullPathElement.GetString() is not { Length: > 0 } fullPath)
                {
                    throw new Exception($"'dotnet msbuild {csprojPath} -getItem:ProjectReference' returned a ProjectReference item for {project} with no resolvable FullPath.");
                }

                result.Add(Path.GetFileNameWithoutExtension(fullPath));
            }

            return result;
        }
    }

    private static bool ProjectExists(string project) => File.Exists(TryResolveCsprojPath(project));

    private static string ResolveCsprojPath(string project)
    {
        var path = TryResolveCsprojPath(project);
        if (path is null || !File.Exists(path))
        {
            throw new Exception($"Could not locate {project}.csproj under {RepositoryRoot()} (src or tests).");
        }

        return path;
    }

    private static string? TryResolveCsprojPath(string project)
    {
        var root = RepositoryRoot();
        var srcPath = Path.Combine(root, "src", project, $"{project}.csproj");
        if (File.Exists(srcPath))
        {
            return srcPath;
        }

        var testsPath = Path.Combine(root, "tests", project, $"{project}.csproj");
        return File.Exists(testsPath) ? testsPath : srcPath;
    }

    private static string? _repositoryRoot;

    private static string RepositoryRoot()
    {
        if (_repositoryRoot is not null)
        {
            return _repositoryRoot;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PalworldServerManager.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new Exception($"Could not locate PalworldServerManager.sln above {AppContext.BaseDirectory}.");
        }

        _repositoryRoot = directory.FullName;
        return _repositoryRoot;
    }
}
