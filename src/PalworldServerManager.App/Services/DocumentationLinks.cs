using System.Diagnostics;
using System.Windows;

namespace PalworldServerManager.App.Services;

/// <summary>Centralizes public documentation URLs so they don't get hardcoded across dozens of event handlers.</summary>
public static class DocumentationLinks
{
    private const string Base = "https://shoemin.github.io/Palworld-Server-Manager/";

    public static string Home => Base;
    public static string GettingStarted => Base + "getting-started/first-server/";
    public static string Lan => Base + "guide/lan/";
    public static string Troubleshooting => Base + "troubleshooting/";
    public static string ReportBug => "https://github.com/shoemin/Palworld-Server-Manager/issues";
    public static string Releases => "https://github.com/shoemin/Palworld-Server-Manager/releases";

    public static void Open(string url, Window? owner = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Could not open the documentation link.\n\n{url}\n\n{ex.Message}", "Documentation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
