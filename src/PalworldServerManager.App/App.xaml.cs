using System.Windows;
using System.Windows.Threading;
using PalworldServerManager.Core.Models;
using Velopack;

namespace PalworldServerManager.App;

public partial class App : System.Windows.Application
{
    public static AppServices Services { get; private set; } = null!;

    /// <summary>
    /// Custom entry point (App.xaml is a Page, not the SDK-default ApplicationDefinition) so
    /// VelopackApp.Build().Run() executes before any WPF/AppServices startup cost. During
    /// install/update/uninstall, Velopack's fast-exit hooks run and the process exits from
    /// inside Run() - so nothing below this line executes for those operations, and no
    /// Palworld/SteamCMD/LAN/WPF subsystem is ever initialized just to apply an update.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Services = new AppServices();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Services.Logger.Info($"Application startup. Args=[{string.Join(", ", e.Args.Select(SanitizeArg))}]");
        base.OnStartup(e);
        _ = ReconcileRuntimeAsync();
        _ = StartLanAsync();
    }

    /// <summary>
    /// Looks for managed PalServer processes that are already running (e.g. surviving a
    /// Manager self-update restart, or simply because the user relaunched the Manager while
    /// Palworld kept running) and reattaches lifetime monitoring to them. A runtime handoff
    /// left by an update, if present and still fresh, narrows this to specific expected
    /// processes; general reconciliation still applies with no handoff at all.
    /// </summary>
    private static async Task ReconcileRuntimeAsync()
    {
        try
        {
            var handoff = await Services.RuntimeHandoff.ConsumeAsync();
            var profiles = await Services.Registry.LoadAsync();
            foreach (var profile in profiles)
            {
                var hint = handoff?.Servers.FirstOrDefault(s => s.ProfileId == profile.Id);
                var outcome = await Services.Processes.ReconcileAsync(profile, hint);
                if (outcome is ReconcileOutcome.Attached or ReconcileOutcome.ExitedDuringGap)
                    Services.Logger.Info($"Startup runtime reconciliation for '{profile.Name}': {outcome}.");
            }
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Startup runtime reconciliation failed. An already-running managed server may show as unmonitored until it is manually refreshed.", ex);
        }
    }

    private static async Task StartLanAsync()
    {
        try
        {
            await Services.Lan.StartIfEnabledAsync();
            if (Services.Lan.Enabled)
                Services.Logger.Info("Persisted LAN services setting is enabled.");
        }
        catch (Exception ex)
        {
            Services.Logger.Error("LAN services failed to start. The application will continue with LAN disabled for this session.", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Services?.Lan.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            try { Services?.Logger.Error("LAN services failed to stop cleanly during application exit.", ex); } catch { }
        }

        try { Services?.Logger.Info($"Application exit. ExitCode={e.ApplicationExitCode}"); } catch { }
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { Services?.Logger.Error("Unhandled WPF dispatcher exception.", e.Exception); } catch { }
    }

    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            if (e.ExceptionObject is Exception ex)
                Services?.Logger.Error($"Unhandled AppDomain exception. IsTerminating={e.IsTerminating}", ex);
            else
                Services?.Logger.Error($"Unhandled AppDomain exception object: {e.ExceptionObject}. IsTerminating={e.IsTerminating}");
        }
        catch { }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try { Services?.Logger.Error("Unobserved Task exception.", e.Exception); } catch { }
        e.SetObserved();
    }

    private static string SanitizeArg(string arg)
    {
        if (arg.Contains("password", StringComparison.OrdinalIgnoreCase)
            || arg.Contains("token", StringComparison.OrdinalIgnoreCase)
            || arg.Contains("secret", StringComparison.OrdinalIgnoreCase))
            return "***REDACTED-ARG***";
        return arg;
    }
}
