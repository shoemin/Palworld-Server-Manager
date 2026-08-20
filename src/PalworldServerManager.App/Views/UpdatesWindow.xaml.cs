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

        var busy = status.State is UpdateState.Checking or UpdateState.Downloading;
        CheckButton.IsEnabled = installed && !busy;
        StableChannelRadio.IsEnabled = installed && !busy;
        PrereleaseChannelRadio.IsEnabled = installed && !busy;
        DownloadButton.IsEnabled = installed && status.State == UpdateState.UpdateAvailable;
        CancelDownloadButton.Visibility = status.State == UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;

        DownloadProgress.Visibility = status.State == UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.Value = status.DownloadPercent;

        StatusMessageText.Text = status.State switch
        {
            UpdateState.Idle when status.LastCheckedUtc is not null => "You're up to date.",
            UpdateState.Idle => installed ? "Check for updates to see if a newer version is available." : string.Empty,
            UpdateState.Checking => "Checking for updates...",
            UpdateState.UpdateAvailable => $"Version {status.AvailableRelease?.Version} is available.",
            UpdateState.Downloading => $"Downloading... {status.DownloadPercent}%",
            UpdateState.ReadyToInstall => "Update downloaded and ready to install. Installing and restarting the Manager will be available in a future update phase.",
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
