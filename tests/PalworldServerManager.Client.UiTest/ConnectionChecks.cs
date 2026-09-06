using System.Security.Authentication;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Grpc.Core;
using PalworldServerManager.Client.Avalonia;
using PalworldServerManager.Client.Avalonia.Shell;
using PalworldServerManager.Client.Avalonia.Views;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Security;

namespace PalworldServerManager.Client.UiTest;

public static partial class Program
{
    private static void ConnectionChecks(string output)
    {
        var expected = new LocalConnectionInfo(Guid.Parse("11111111-1111-1111-1111-111111111111"), new(Guid.Parse("22222222-2222-2222-2222-222222222222"), false));
        var calls = 0; Func<CancellationToken, Task<LocalConnectionInfo>> reply = _ => Task.FromResult(expected);
        var window = new MainWindow(ct => { calls++; return reply(ct); }) { Width = 800 };
        try
        {
            window.Show(); Click(window, "ConnectLocal");
            Check(window.VerifiedConnection == expected && window.State?.LocalHost.Value == expected.HostId && !window.IsConnecting, "Connection lost verified semantic identity.");
            Check(Find<ServerTree>(window, "ServerTree").Items.Count == 0 && Find<SelectableTextBlock>(window, "LocalHostIdentity").Text!.Contains(expected.HostId.ToString("D")), "Connection invented inventory or hid exact Host.");
            Check(Find<TextBlock>(window, "LocalConnectionStatus").Text!.Contains("inventory is unavailable"), "Empty inventory was represented as no servers.");
            foreach (var theme in Enum.GetValues<ShellTheme>()) { window.SetTheme(theme); Capture(window, output, "connection-" + theme); }
            window.Width = 640; window.FontSize = 28; Capture(window, output, "connection-enlarged");
            Find<Button>(window, "ConnectLocal").BringIntoView(); Dispatcher.UIThread.RunJobs();
            Capture(window, output, "connection-enlarged");
            var connect = Find<Button>(window, "ConnectLocal"); var position = connect.TranslatePoint(default, window)!.Value;
            Check(position.X >= 0 && position.X + connect.Bounds.Width <= window.Width && position.Y >= 0 && position.Y + connect.Bounds.Height <= window.Height, "Enlarged connection control is unreachable.");
            window.FontSize = 14; window.Width = 800; window.UpdateLayout();
            foreach (var failure in new Exception[] { new AuthenticationException("SECRET"), new RpcException(new(StatusCode.Unauthenticated, "SECRET")),
                new ClientActivationException(HostActivationStatus.AccessDenied), new LocalHostTrustUnavailableException("SECRET"),
                new LocalHostAuthenticationException("SECRET"), new IOException("SECRET") })
            {
                reply = _ => Task.FromException<LocalConnectionInfo>(failure); Click(window, "ConnectLocal");
                Check(window.VerifiedConnection is null && window.State is null && !Find<SelectableTextBlock>(window, "LocalHostIdentity").IsVisible, "Failed reconnect retained a verified identity.");
                Check(!window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("SECRET") == true), "UI disclosed exception detail.");
            }
            Capture(window, output, "connection-failure");
            reply = _ => Task.FromResult(default(LocalConnectionInfo)); Click(window, "ConnectLocal"); Check(window.VerifiedConnection is null, "Malformed reply accepted.");
            var pending = new TaskCompletionSource<LocalConnectionInfo>(); CancellationToken captured = default;
            reply = ct => { captured = ct; return pending.Task; }; Click(window, "ConnectLocal"); var atStart = calls;
            _ = window.ConnectLocalAsync(); Check(calls == atStart && window.IsConnecting && !Find<Button>(window, "ConnectLocal").IsEnabled, "Concurrent connect was not single-flight.");
            Capture(window, output, "connection-pending"); Click(window, "CancelLocalConnection"); Check(captured.IsCancellationRequested, "Cancel did not signal the client request.");
            pending.SetResult(expected); Dispatcher.UIThread.RunJobs();
            Check(window.VerifiedConnection is null && window.State is null && !window.IsConnecting && Find<Button>(window, "ConnectLocal").IsFocused && Find<TextBlock>(window, "LocalConnectionStatus").Text!.Contains("Host may still be running"), "Canceled late success established identity, lost focus or claimed Host rollback.");
            pending = new(); reply = ct => { captured = ct; return pending.Task; }; Click(window, "ConnectLocal"); window.Close(); pending.SetResult(expected); Dispatcher.UIThread.RunJobs();
            Check(captured.IsCancellationRequested && window.VerifiedConnection is null && !window.IsConnecting, "Closed window retained late connection state.");
        }
        finally { window.Close(); }
    }
}
