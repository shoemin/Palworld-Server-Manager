using System.Windows;
using PalworldServerManager.App.Services;
using PalworldServerManager.Core.Models;

namespace PalworldServerManager.App.Views;

public partial class UpdatesWindow : Window
{
    private readonly AppServices _services;
    private bool _suppressChannelEvents;
    private CancellationTokenSource? _downloadCts;

    public UpdatesWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        Loaded += (_, _) =>
        {
            _services.Updates.StatusChanged += Updates_StatusChanged;
            RefreshUi();
        };
        Closed += (_, _) =>
        {
            _services.Updates.StatusChanged -= Updates_StatusChanged;
            _downloadCts?.Cancel();
        };
    }

    private void Updates_StatusChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(RefreshUi);

    private void RefreshUi()
    {
        var status = _services.Updates.Status;

        CurrentVersionText.Text = status.CurrentVersion;
        ExecutionModeText.Text = status.ExecutionMode.ToString();
        LastCheckedText.Text = status.LastCheckedUtc?.ToLocalTime().ToString("g") ?? "Never";
        AvailableVersionText.Text = status.AvailableRelease?.Version ?? "—";
        DownloadSizeText.Text = status.AvailableRelease?.SizeBytes is long bytes ? FormatBytes(bytes) : "—";
        ReleaseNotesText.Text = status.AvailableRelease?.ReleaseNotes ?? "(No release notes available.)";
        StateText.Text = status.State.ToString();

        _suppressChannelEvents = true;
        StableChannelRadio.IsChecked = status.Channel == UpdateChannel.Stable;
        PrereleaseChannelRadio.IsChecked = status.Channel == UpdateChannel.Prerelease;
        _suppressChannelEvents = false;

        var installed = status.ExecutionMode == UpdateExecutionMode.Installed;
        ModeBannerText.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        ModeBannerText.Text = status.ExecutionMode switch
        {
            UpdateExecutionMode.Portable => "This portable copy cannot install updates automatically. Install Palworld Server Manager using Setup.exe to enable automatic updates, or download the latest version manually.",
            UpdateExecutionMode.Development => "Development build — self-update disabled.",
            _ => string.Empty
        };

        var busy = status.State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Applying;
        CheckButton.IsEnabled = installed && !busy;
        StableChannelRadio.IsEnabled = installed && !busy;
        PrereleaseChannelRadio.IsEnabled = installed && !busy;
        DownloadButton.IsEnabled = installed && status.State == UpdateState.UpdateAvailable;
        CancelDownloadButton.Visibility = status.State == UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;

        DownloadProgress.Visibility = status.State == UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.Value = status.DownloadPercent;

        var applyBlockReason = installed ? _services.Updates.GetApplyBlockReason() : "Not installed.";
        InstallButton.Visibility = status.State is UpdateState.ReadyToInstall or UpdateState.Applying ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = installed && status.State == UpdateState.ReadyToInstall && applyBlockReason is null;

        StatusMessageText.Text = status.State switch
        {
            UpdateState.Idle when status.LastCheckedUtc is not null => "You're up to date.",
            UpdateState.Idle => installed ? "Check for updates to see if a newer version is available." : string.Empty,
            UpdateState.Checking => "Checking for updates...",
            UpdateState.UpdateAvailable => $"Version {status.AvailableRelease?.Version} is available.",
            UpdateState.Downloading => $"Downloading... {status.DownloadPercent}%",
            UpdateState.ReadyToInstall when applyBlockReason is not null => applyBlockReason,
            UpdateState.ReadyToInstall => "Update downloaded and ready to install. Palworld servers you have running will not be affected.",
            UpdateState.Applying => "Applying the update and restarting Palworld Server Manager. Any running Palworld server is not affected and will keep running.",
            UpdateState.Failed => status.ErrorMessage ?? "The update operation failed.",
            _ => string.Empty
        };
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await _services.Updates.CheckForUpdatesAsync();

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
        await _services.Updates.DownloadUpdateAsync(_downloadCts.Token);
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => _downloadCts?.Cancel();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var blockReason = _services.Updates.GetApplyBlockReason();
        if (blockReason is not null)
        {
            MessageBox.Show(this, blockReason, "Install and Restart", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmed = MessageBox.Show(this,
            "Palworld Server Manager will close and reopen to finish installing this update.\n\n" +
            "Any Palworld server you have running will keep running the entire time. This does not save, stop, or restart your Palworld server, and connected players should not be disconnected by it.\n\n" +
            "The Manager's Dashboard and LAN pairing will be briefly unavailable while it restarts.\n\n" +
            "Continue?",
            "Install and Restart",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
        if (!confirmed) return;

        var result = await _services.Updates.ApplyAndRestartAsync();
        if (!result.Success)
        {
            MessageBox.Show(this, result.Message, "Install and Restart", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // The external updater is now waiting for this process to exit; Velopack applies the
        // staged update and relaunches the Manager once we're gone. Palworld itself was never
        // touched by any of this.
        Application.Current.Shutdown();
    }

    private void ChannelRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressChannelEvents) return;
        _services.Updates.SetChannel(StableChannelRadio.IsChecked == true ? UpdateChannel.Stable : UpdateChannel.Prerelease);
    }

    private void OpenReleases_Click(object sender, RoutedEventArgs e) => DocumentationLinks.Open(DocumentationLinks.Releases, this);

    private static string FormatBytes(long bytes)
    {
        double mb = bytes / 1024.0 / 1024.0;
        return mb >= 1 ? $"{mb:F1} MB" : $"{bytes / 1024.0:F0} KB";
    }
}
