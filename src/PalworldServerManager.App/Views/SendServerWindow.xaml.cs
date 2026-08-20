using System.IO;
using System.Windows;
using PalworldServerManager.Core.Models;
using PalworldServerManager.Lan;

namespace PalworldServerManager.App.Views;

public partial class SendServerWindow : Window
{
    private readonly AppServices _services;
    private readonly ServerProfile _profile;
    private CancellationTokenSource? _cts;

    public SendServerWindow(AppServices services, ServerProfile profile)
    {
        InitializeComponent();
        _services = services;
        _profile = profile;
        ServerText.Text = $"Send {_profile.Name}";
        Loaded += (_, _) => RefreshDestinations();
        Closed += (_, _) => _cts?.Cancel();
    }

    private void RefreshDestinations()
    {
        var peers = _services.Lan.GetPeers().Where(x => x.IsPaired).ToList();
        PeerCombo.ItemsSource = peers;
        PeerCombo.DisplayMemberPath = nameof(LanPeer.DisplayName);
        if (peers.Count > 0) PeerCombo.SelectedIndex = 0;
        StatusText.Text = peers.Count == 0
            ? "No paired Managers are currently visible. Enable LAN on both PCs and pair them first."
            : $"{peers.Count} paired destination(s) available.";
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (!_services.Lan.Running)
        {
            MessageBox.Show(this, "Enable LAN services first.", "Send Server", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (PeerCombo.SelectedItem is not LanPeer peer)
        {
            MessageBox.Show(this, "Select a paired destination.", "Send Server", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_services.Processes.IsRunning(_profile)
            && MessageBox.Show(this,
                "This server is running. Creating a consistent .palserver package requires a safe save and stop. Continue?",
                "Stop and Send",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        SendButton.IsEnabled = false;
        PeerCombo.IsEnabled = false;
        _cts = new CancellationTokenSource();

        var temp = Path.Combine(_services.Paths.OutgoingRoot,
            $"{_profile.Id:D}_{DateTime.UtcNow:yyyyMMddHHmmss}.palserver");

        try
        {
            Directory.CreateDirectory(_services.Paths.OutgoingRoot);
            StatusText.Text = "Creating portable .palserver package...";
            await _services.Packages.ExportAsync(_profile, temp, _cts.Token);

            var status = new Progress<string>(s => StatusText.Text = s);
            var progress = new Progress<double>(p => TransferProgress.Value = Math.Clamp(p * 100.0, 0, 100));
            await _services.Lan.Client.SendPackageAsync(peer, _profile.Name, temp, status, progress, _cts.Token);

            TransferProgress.Value = 100;
            MessageBox.Show(this,
                $"'{_profile.Name}' was transferred to {peer.MachineName} and verified by SHA-256.\n\nThe destination can now import it from LAN & Transfers.",
                "Transfer Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Transfer canceled.";
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"LAN send failed. Server='{_profile.Name}' Destination='{peer.MachineName}'", ex);
            StatusText.Text = "Failed: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Transfer Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            SendButton.IsEnabled = true;
            PeerCombo.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDestinations();
}
