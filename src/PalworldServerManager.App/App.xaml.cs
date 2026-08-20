using System.Windows;
using System.Windows.Threading;

namespace PalworldServerManager.App;

public partial class App : System.Windows.Application
{
    public static AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        Services = new AppServices();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Services.Logger.Info($"Application startup. Args=[{string.Join(", ", e.Args.Select(SanitizeArg))}]");
        base.OnStartup(e);
        _ = StartLanAsync();
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
