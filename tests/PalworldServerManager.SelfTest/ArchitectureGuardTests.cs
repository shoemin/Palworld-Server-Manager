using System.Text.RegularExpressions;

namespace PalworldServerManager.SelfTest;

// Structural dependency-direction guards for the v0.5 topology accepted by #19
// (docs/developer/v0.5-architecture.md SS1) and scaffolded by #39. These read each project's
// .csproj <ProjectReference> elements directly from disk rather than loading assemblies, so a
// forbidden reference fails a fast, static check the moment it's added to a .csproj - it doesn't
// require the referencing project to actually build or run first.
public static class ArchitectureGuardTests
{
    private const string Core = "PalworldServerManager.Core";
    private const string Lan = "PalworldServerManager.Lan";
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

    // Every project this issue (#39) is responsible for scaffolding. Host-side Platform.Linux and
    // Client.Platform.Linux are deliberately excluded from this project's own scope (#21).
    private static readonly string[] AllNewV05Projects =
    [
        Contracts, Host, HostPersistence, HostCli, PlatformContracts, PlatformWindows,
        ClientCli, ClientAvalonia, ClientPlatformContracts, ClientPlatformWindows
    ];

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
        AssertNoReferenceToProjectMatching(PlatformWindows, "\\.Linux$");
        AssertNoReferenceToProjectMatching(ClientPlatformWindows, "\\.Linux$");

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

    public static Task TestFrozenWpfAppStillReferencesLanUnchanged()
    {
        // The frozen App project's existing Lan dependency (ARCH-002) must survive this
        // scaffolding round untouched.
        DependsOn("PalworldServerManager.App", Lan);
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

    private static void AssertNoReferenceToProjectMatching(string project, string namePattern)
    {
        var regex = new Regex(namePattern);
        var direct = DirectReferences(project);
        var offender = direct.FirstOrDefault(r => regex.IsMatch(r));
        if (offender is not null)
        {
            throw new Exception($"{project} references {offender}, matching forbidden pattern '{namePattern}'.");
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
        var text = File.ReadAllText(csprojPath);
        var matches = Regex.Matches(text, "<ProjectReference\\s+Include=\"([^\"]+)\"");
        var results = new List<string>();
        foreach (Match match in matches)
        {
            var referencedPath = match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar);
            var referencedName = Path.GetFileNameWithoutExtension(referencedPath);
            results.Add(referencedName);
        }

        return results;
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
