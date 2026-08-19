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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Services?.Logger.Info($"Application exit. ExitCode={e.ApplicationExitCode}"); } catch { }
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { Services?.Logger.Error("Unhandled WPF dispatcher exception.", e.Exception); } catch { }
        // Do not mark handled. Unexpected UI faults should still fail visibly after being logged.
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
