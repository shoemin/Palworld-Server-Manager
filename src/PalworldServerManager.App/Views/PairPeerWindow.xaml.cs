using System.Windows;
using PalworldServerManager.Lan;

namespace PalworldServerManager.App.Views;

public partial class PairPeerWindow : Window
{
    private readonly AppServices _services;
    private readonly LanPeer _peer;

    public PairPeerWindow(AppServices services, LanPeer peer)
    {
        InitializeComponent();
        _services = services;
        _peer = peer;
        PeerText.Text = $"Pair with {peer.MachineName}";
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim();
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            StatusText.Text = "Enter the six-digit pairing code.";
            return;
        }

        PairButton.IsEnabled = false;
        StatusText.Text = "Pairing...";
        try
        {
            await _services.Lan.Client.PairAsync(_peer, code);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            PairButton.IsEnabled = true;
        }
    }
}
