using Avalonia;
using PalworldServerManager.Client.Security;

namespace PalworldServerManager.Client.Avalonia;

// Single interactive Windows composition root. Server feature screens remain separate.
internal static class Program
{
    internal static LocalSecurityClient CreateSecurityClient() => WindowsClientSecurity.Create();
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
