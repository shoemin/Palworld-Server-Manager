using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PalworldServerManager.App.Services;
using PalworldServerManager.Lan;

namespace PalworldServerManager.App.Views;

public partial class LanView : UserControl
{
    private readonly DispatcherTimer _timer;

    public LanView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshUi();
        Loaded += (_, _) =>
        {
            RefreshUi();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private AppServices Services => App.Services;

    private void RefreshUi()
    {
        ThisComputerText.Text = $"{Environment.MachineName} • Instance {Services.Lan.State.InstanceId:D}";
        LanStatusText.Text = Services.Lan.Running
            ? $"LAN service enabled • TCP {Services.Lan.State.ApiPort} • discovery UDP {Services.Lan.State.DiscoveryPort}"
            : "LAN service disabled";
        ToggleLanButton.Content = Services.Lan.Running ? "Disable LAN" : "Enable LAN";
        PeersGrid.ItemsSource = Services.Lan.GetPeers();
        OffersGrid.ItemsSource = Services.Lan.Host.GetOffers();
    }

    private async void ToggleLan_Click(object sender, RoutedEventArgs e)
    {
        ToggleLanButton.IsEnabled = false;
        try
        {
            await Services.Lan.SetEnabledAsync(!Services.Lan.Running);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this),
                ex.Message + "\n\nWindows Firewall may also need to allow Palworld Server Manager on private networks.",
                "LAN Service", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ToggleLanButton.IsEnabled = true;
            RefreshUi();
        }
    }

    private void GenerateCode_Click(object sender, RoutedEventArgs e)
    {
        if (!Services.Lan.Running)
        {
            MessageBox.Show(Window.GetWindow(this), "Enable LAN services first.", "Pairing", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var code = Services.Lan.Pairing.GenerateCode();
        PairingCodeText.Text = $"Pairing code: {code.Code[..3]} {code.Code[3..]}   Expires: {code.ExpiresUtc.ToLocalTime():T}";
    }

    private void PairSelected_Click(object sender, RoutedEventArgs e)
    {
        if (PeersGrid.SelectedItem is not LanPeer peer) return;
        if (peer.IsPaired)
        {
            MessageBox.Show(Window.GetWindow(this), "This Manager is already paired.", "Pairing", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new PairPeerWindow(Services, peer) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
        RefreshUi();
    }

    private void UnpairSelected_Click(object sender, RoutedEventArgs e)
    {
        if (PeersGrid.SelectedItem is not LanPeer peer) return;
        Services.Lan.UnpairPeer(peer.InstanceId);
        RefreshUi();
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (OffersGrid.SelectedItem is not LanTransferOffer offer) return;
        Services.Lan.Host.AcceptOffer(offer.OfferId);
        RefreshUi();
    }

    private void Reject_Click(object sender, RoutedEventArgs e)
    {
        if (OffersGrid.SelectedItem is not LanTransferOffer offer) return;
        Services.Lan.Host.RejectOffer(offer.OfferId);
        RefreshUi();
    }

    private async void ImportReceived_Click(object sender, RoutedEventArgs e)
    {
        if (OffersGrid.SelectedItem is not LanTransferOffer { Status: LanTransferStatus.Received, ReceivedPath: { } path } offer
            || !File.Exists(path))
        {
            MessageBox.Show(Window.GetWindow(this), "Select a completed received transfer first.", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var owner = Window.GetWindow(this);
        if (owner is null || !SteamUiRecovery.ConfirmPreflight(owner, Services)) return;

        IsEnabled = false;
        try
        {
            var progress = new Progress<string>(s => PairingCodeText.Text = s);
            var profile = await Services.Packages.ImportAsync(path, progress);
            PairingCodeText.Text = $"Imported '{profile.Name}' successfully.";
            MessageBox.Show(owner, $"Imported '{profile.Name}' as a new managed server.", "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Services.Logger.Error($"Import of received LAN package failed. Offer={offer.OfferId}", ex);
            MessageBox.Show(owner, ex.Message, "Import Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
            RefreshUi();
        }
    }

    private void OpenIncoming_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Services.Paths.IncomingRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Services.Paths.IncomingRoot}\"") { UseShellExecute = true });
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshUi();
}
