using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PalworldServerManager.Client.Security;

namespace PalworldServerManager.Client.Avalonia;

public partial class App : Application
{
    private readonly Lazy<LocalSecurityClient> _security = new(Program.CreateSecurityClient);
    // Later authorized screens share the ordinary CLI's exact transport and per-user boundary.
    public LocalSecurityClient LocalSecurity => _security.Value;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
