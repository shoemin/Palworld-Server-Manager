using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Services;

namespace PalworldServerManager.App;

public sealed class AppServices
{
    public AppServices()
    {
        Paths = new AppPaths();
        Paths.EnsureCreated();
        Logger = new FileLogger(Paths);
        Registry = new ProfileRegistry(Paths, Logger);
        Settings = new PalworldSettingsService(Logger);
        SteamLocator = new SteamLocator(Paths, Logger);
        Discovery = new ServerDiscoveryService(SteamLocator, Registry, Logger);
        SteamCmd = new SteamCmdService(Paths, SteamLocator, Logger);
        Rest = new PalworldRestClient(Logger);
        Processes = new ServerProcessService(Settings, Rest, Logger);
        Provisioning = new ServerProvisioningService(Paths, SteamCmd, Settings, Registry, Logger);
        ExistingImport = new ExistingServerImportService(Paths, Discovery, Registry, SteamCmd, Logger);
        Backups = new BackupService(Paths, Processes, Logger);
        Packages = new PortablePackageService(Paths, Processes, SteamCmd, Registry, Logger);
        Diagnostics = new DiagnosticBundleService(Paths, Logger);
    }

    public AppPaths Paths { get; }
    public IAppLogger Logger { get; }
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
}
