using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using PalworldServerManager.Client.Avalonia;
using PalworldServerManager.Client.Avalonia.Views;

namespace PalworldServerManager.Client.UiTest;

public static partial class Program
{
    // Explicit integration mode, called by the existing isolated Windows runner fixture.
    // No injected transport/result, enrollment, privileged operation or secret output.
    private static async Task<int> ActualLocalConnection()
    {
        var window = ((App)Application.Current!).CreateMainWindow();
        try
        {
            window.Show(); Check(window.State is null && window.VerifiedConnection is null, "Unexpected initial UI state.");
            var elapsed = Stopwatch.StartNew(); Click(window, "ConnectLocal");
            while (window.IsConnecting)
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException();
                await Task.Delay(10);
            }
            if (window.VerifiedConnection is not { } verified)
            {
                Check(window.State is null && !Find<SelectableTextBlock>(window, "LocalHostIdentity").IsVisible &&
                    Find<ServerTree>(window, "ServerTree").Items.Count == 0, "Failed actual connection retained identity or inventory.");
                Console.Error.WriteLine(Find<TextBlock>(window, "LocalConnectionStatus").Text); return 1;
            }
            Check(window.State?.LocalHost.Value == verified.HostId && Find<ServerTree>(window, "ServerTree").Items.Count == 0 &&
                Find<SelectableTextBlock>(window, "LocalHostIdentity").Text!.Contains(verified.HostId.ToString("D")) &&
                Find<TextBlock>(window, "LocalConnectionStatus").Text!.Contains("inventory is unavailable"), "Actual UI result differs from verified identity.");
            Console.WriteLine(JsonSerializer.Serialize(new { hostId = verified.HostId, localPrincipalId = verified.Identity.LocalPrincipalId, isOwner = verified.Identity.IsOwner }));
            return 0;
        }
        finally { window.Close(); }
    }
}
