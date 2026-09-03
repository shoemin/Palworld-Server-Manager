using System.Xml.Linq;

namespace PalworldServerManager.SelfTest;

// Structural dependency-direction guards for the v0.5 topology accepted by #19
// (docs/developer/v0.5-architecture.md SS1) and scaffolded by #39. These read each project's
// .csproj <ProjectReference> elements directly from disk (via System.Xml.Linq, not assembly
// loading), so a forbidden or missing reference fails a fast, static check the moment it's
// changed in a .csproj - it doesn't require the referencing project to actually build first.
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
        // The frozen App project's existing Lan dependency (ARCH-002) must survive this
        // scaffolding round untouched.
        DependsOn(App, Lan);
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

    private static List<string> DirectReferences(string project)
    {
        var csprojPath = ResolveCsprojPath(project);
        var doc = XDocument.Load(csprojPath);
        return doc.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrEmpty(include))
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', Path.DirectorySeparatorChar)))
            .ToList();
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
