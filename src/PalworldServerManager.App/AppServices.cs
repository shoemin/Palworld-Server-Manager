using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Services;
using PalworldServerManager.Core.Services.Update;
using PalworldServerManager.Lan;

namespace PalworldServerManager.App;

public sealed class AppServices
{
    public AppServices()
    {
        Paths = new AppPaths();
        Paths.EnsureCreated();
        Logger = new FileLogger(Paths);
        Operations = new CriticalOperationTracker();
        Registry = new ProfileRegistry(Paths, Logger);
        Settings = new PalworldSettingsService(Logger, Operations);
        SteamLocator = new SteamLocator(Paths, Logger);
        Discovery = new ServerDiscoveryService(SteamLocator, Registry, Logger);
        SteamCmd = new SteamCmdService(Paths, SteamLocator, Logger);
        Rest = new PalworldRestClient(Logger);
        Processes = new ServerProcessService(Settings, Rest, Logger, Operations);
        Provisioning = new ServerProvisioningService(Paths, SteamCmd, Settings, Registry, Logger, Operations);
        ExistingImport = new ExistingServerImportService(Paths, Discovery, Registry, SteamCmd, Logger, Operations);
        Backups = new BackupService(Paths, Processes, Logger, Operations);
        Packages = new PortablePackageService(Paths, Processes, SteamCmd, Registry, Logger, Operations);
        Diagnostics = new DiagnosticBundleService(Paths, Logger);
        Dashboard = new DashboardService(Paths, Settings, Rest, Processes, Logger);
        Lan = new LanCoordinator(Paths, Registry, Dashboard, Processes, Logger, Operations);
        RuntimeHandoff = new RuntimeHandoffService(Paths, Logger);
        Updates = new ApplicationUpdateService(new VelopackUpdateBackend(Logger), Paths, Logger, Operations, Registry, Processes, RuntimeHandoff)
        {
            // Manager-only background services that must stop before restart and, if the apply
            // ultimately fails after they've stopped, resume afterward. Palworld is never
            // touched by either callback - LanCoordinator only owns the Kestrel host/UDP
            // discovery, never the game server process.
            PreRestartShutdownAsync = _ => Lan.StopAsync(),
            PostFailureRecoveryAsync = Lan.StartIfEnabledAsync
        };
    }

    public AppPaths Paths { get; }
    public IAppLogger Logger { get; }
    public ICriticalOperationTracker Operations { get; }
    public ProfileRegistry Registry { get; }
    public PalworldSettingsService Settings { get; }
    public SteamLocator SteamLocator { get; }
    public ServerDiscoveryService Discovery { get; }
    public SteamCmdService SteamCmd { get; }
    public PalworldRestClient Rest { get; }
    public ServerProcessService Processes { get; }
    public ServerProvisioningService Provisioning { get; }
    public ExistingServerImportService ExistingImport { get; }
    public BackupService Backups { get; }
    public PortablePackageService Packages { get; }
    public DiagnosticBundleService Diagnostics { get; }
    public DashboardService Dashboard { get; }
    public LanCoordinator Lan { get; }
    public RuntimeHandoffService RuntimeHandoff { get; }
    public ApplicationUpdateService Updates { get; }
}
