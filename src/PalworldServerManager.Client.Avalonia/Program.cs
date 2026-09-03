using Avalonia;

namespace PalworldServerManager.Client.Avalonia;

// Composition-root scaffold only (#39). Shell/theming/server-management UI content
// (#52-#54) is out of scope here.
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
