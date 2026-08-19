using System.Windows;

namespace PalworldServerManager.App.Views;

public partial class CreateServerWindow : Window
{
    public CreateServerWindow() => InitializeComponent();

    public string ServerName => NameBox.Text.Trim();
    public int GamePort { get; private set; }
    public int RestPort { get; private set; }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ServerName))
        {
            MessageBox.Show(this, "Enter a server name.");
            return;
        }
        if (!int.TryParse(GamePortBox.Text, out var gamePort) || gamePort is < 1 or > 65535)
        {
            MessageBox.Show(this, "Game port must be between 1 and 65535.");
            return;
        }
        if (!int.TryParse(RestPortBox.Text, out var restPort) || restPort is < 1 or > 65535)
        {
            MessageBox.Show(this, "REST API port must be between 1 and 65535.");
            return;
        }
        if (gamePort == restPort)
        {
            MessageBox.Show(this, "Game and REST API ports must be different.");
            return;
        }
        GamePort = gamePort;
        RestPort = restPort;
        DialogResult = true;
    }
}
