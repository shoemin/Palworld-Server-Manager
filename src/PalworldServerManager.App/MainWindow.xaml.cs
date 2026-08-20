using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using PalworldServerManager.App.Services;
using PalworldServerManager.App.Views;
using PalworldServerManager.Core.Models;
using PalworldServerManager.Core.Services;

namespace PalworldServerManager.App;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private List<ServerProfile> _profiles = [];
    private bool _busy;
    private readonly DispatcherTimer _statusTimer;

    public MainWindow()
    {
        InitializeComponent();
        _services = App.Services;
        _services.Processes.ServerLifetimeEnded += Processes_ServerLifetimeEnded;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RefreshSelectedDetails();
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private ServerProfile? Selected => ServerList.SelectedItem as ServerProfile;

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        _services.Processes.ServerLifetimeEnded -= Processes_ServerLifetimeEnded;
    }

    private void Processes_ServerLifetimeEnded(object? sender, ServerProcessLifetimeEndedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            await RefreshProfilesAsync(e.ServerId);
            OperationText.Text = e.Message;
            if (!e.ExpectedStop && e.HasNonZeroExitCode)
            {
                MessageBox.Show(this,
                    e.Message + "\n\nUse Export Diagnostic Bundle if you want to send the crash logs for review.",
                    "Palworld Server Exited",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }));
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _statusTimer.Start();
        await RefreshProfilesAsync();
        if (_profiles.Count == 0 && MessageBox.Show(this, "No managed servers are registered. Scan the expected Steam/SteamCMD locations for an existing Palworld dedicated server?", "First Run", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            var dialog = new DiscoveryWindow(_services) { Owner = this };
            dialog.ShowDialog();
            await RefreshProfilesAsync(dialog.LastImportedId);
        }
    }

    private async Task RefreshProfilesAsync(Guid? selectId = null)
    {
        var previous = Selected?.Id;
        _profiles = await _services.Registry.LoadAsync();
        ServerList.ItemsSource = null;
        ServerList.ItemsSource = _profiles;
        var target = selectId ?? previous;
        if (target.HasValue)
            ServerList.SelectedItem = _profiles.FirstOrDefault(x => x.Id == target.Value);
        if (ServerList.SelectedItem is null && _profiles.Count > 0)
            ServerList.SelectedIndex = 0;
        RefreshSelectedDetails();
    }

    private void RefreshSelectedDetails()
    {
        var profile = Selected;
        if (profile is null)
        {
            ServerNameText.Text = "Select a server";
            StatusText.Text = "—";
            GamePortText.Text = "—";
            RestPortText.Text = "—";
            InstallPathText.Text = string.Empty;
            ImportedFromText.Text = string.Empty;
            CreatedText.Text = string.Empty;
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            ForceStopButton.IsEnabled = false;
            return;
        }

        ServerNameText.Text = profile.Name;
        var running = _services.Processes.IsRunning(profile);
        StatusText.Text = _services.Processes.GetStatusText(profile);
        StartButton.IsEnabled = !_busy && !running;
        StopButton.IsEnabled = !_busy && running;
        ForceStopButton.IsEnabled = !_busy && running;
        GamePortText.Text = profile.GamePort.ToString();
        RestPortText.Text = profile.RestApiPort.ToString();
        InstallPathText.Text = profile.InstallPath;
        ImportedFromText.Text = profile.ImportedFrom ?? "Created by Palworld Server Manager";
        CreatedText.Text = profile.CreatedUtc.ToLocalTime().ToString("g");
    }

    private async Task RunBusyAsync(
        string operationName,
        string initialStatus,
        Func<Task> action,
        ServerProfile? logProfile = null,
        bool requiresSteamCmd = false)
    {
        if (_busy) return;
        if (requiresSteamCmd && !SteamUiRecovery.ConfirmPreflight(this, _services))
        {
            OperationText.Text = "Operation canceled before SteamCMD provisioning.";
            return;
        }

        _busy = true;
        IsEnabled = false;
        OperationText.Text = initialStatus;
        using var operation = _services.Logger.BeginOperation(operationName, logProfile?.Id, logProfile?.Name);
        _services.Logger.Info($"UI operation started. Status='{initialStatus}' RequiresSteamCmd={requiresSteamCmd}");
        var recoveryRetries = 0;
        try
        {
            while (true)
            {
                try
                {
                    await action();
                    _services.Logger.Info($"UI operation completed. FinalStatus='{OperationText.Text}'");
                    break;
                }
                catch (SteamCmdException ex) when (requiresSteamCmd && ex.SuggestSteamClientRecovery && recoveryRetries == 0)
                {
                    _services.Logger.Warning($"UI operation '{operationName}' encountered recoverable SteamCMD exit code {ex.ExitCode}.");
                    IsEnabled = true;
                    var retry = SteamUiRecovery.PromptRetryAfterFailure(this, _services, ex);
                    IsEnabled = false;
                    if (!retry) throw;

                    recoveryRetries++;
                    OperationText.Text = "Retrying operation after Steam preflight...";
                    _services.Logger.Info($"Retrying UI operation '{operationName}' once after SteamCMD recovery preflight.");
                }
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"UI operation '{operationName}' failed.", ex);
            MessageBox.Show(this, ex.Message, "Palworld Server Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            OperationText.Text = "Failed: " + ex.Message;
        }
        finally
        {
            IsEnabled = true;
            _busy = false;
            await RefreshProfilesAsync(Selected?.Id);
        }
    }

    private async void CreateServer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CreateServerWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Guid? created = null;
        await RunBusyAsync("CreateServer", "Creating server...", async () =>
        {
            var progress = new Progress<string>(s => OperationText.Text = s);
            var profile = await _services.Provisioning.CreateAsync(dialog.ServerName, dialog.GamePort, dialog.RestPort, progress);
            created = profile.Id;
            OperationText.Text = "Server created successfully.";
        }, requiresSteamCmd: true);
        if (created.HasValue) await RefreshProfilesAsync(created);
    }

    private async void DiscoverServers_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DiscoveryWindow(_services) { Owner = this };
        dialog.ShowDialog();
        await RefreshProfilesAsync(dialog.LastImportedId);
    }

    private async void ImportPackage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Palworld Server Package (*.palserver)|*.palserver|All files (*.*)|*.*" };
        if (picker.ShowDialog(this) != true) return;
        Guid? imported = null;
        await RunBusyAsync("ImportPortablePackage", "Importing portable server package...", async () =>
        {
            var progress = new Progress<string>(s => OperationText.Text = s);
            var profile = await _services.Packages.ImportAsync(picker.FileName, progress);
            imported = profile.Id;
            OperationText.Text = "Portable server imported successfully.";
        }, requiresSteamCmd: true);
        if (imported.HasValue) await RefreshProfilesAsync(imported);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile) return;
        await RunBusyAsync("StartServer", "Starting server...", async () =>
        {
            var result = await _services.Processes.StartAsync(profile, _profiles);
            OperationText.Text = result.Message;
            if (!result.Success) MessageBox.Show(this, result.Message, "Start Server", MessageBoxButton.OK, MessageBoxImage.Warning);
        }, profile);
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile) return;
        await RunBusyAsync("StopServerSafely", "Saving and stopping server...", async () =>
        {
            var result = await _services.Processes.StopAsync(profile, force: false);
            OperationText.Text = result.Message;
            if (!result.Success) MessageBox.Show(this, result.Message, "Safe Stop", MessageBoxButton.OK, MessageBoxImage.Warning);
        }, profile);
    }

    private async void ForceStop_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile) return;
        if (MessageBox.Show(this, "Force Stop does not request a world save first. Use it only when a graceful stop is unavailable. Continue?", "Force Stop", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunBusyAsync("ForceStopServer", "Force-stopping server...", async () =>
        {
            var result = await _services.Processes.StopAsync(profile, force: true);
            OperationText.Text = result.Message;
        }, profile);
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile) return;
        if (_services.Processes.IsRunning(profile))
        {
            MessageBox.Show(this, "Stop the server before changing settings.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new SettingsWindow(_services, profile) { Owner = this }.ShowDialog();
        await RefreshProfilesAsync(profile.Id);
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile) return;
        await RunBusyAsync("UpdateServer", "Preparing server update...", async () =>
        {
            if (_services.Processes.IsRunning(profile))
            {
                var stop = await _services.Processes.StopAsync(profile, false);
                if (!stop.Success) throw new InvalidOperationException(stop.Message);
            }
            OperationText.Text = "Creating pre-update backup...";
            await _services.Backups.CreateBackupAsync(profile, "pre-update");
            var progress = new Progress<string>(s => OperationText.Text = s);
            await _services.Provisioning.UpdateAsync(profile, progress);
            OperationText.Text = "Server update/validation completed.";
        }, profile, requiresSteamCmd: true);
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile) return;
        await RunBusyAsync("CreateBackup", "Creating backup...", async () =>
        {
            var output = await _services.Backups.CreateBackupAsync(profile);
            OperationText.Text = "Backup created: " + output;
        }, profile);
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile) return;
        var picker = new OpenFileDialog
        {
            Filter = "Backup ZIP (*.zip)|*.zip|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(_services.Paths.BackupsRoot, profile.Id.ToString("D"))
        };
        if (picker.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, "Restoring will replace the current managed save/mod data. A pre-restore backup will be created automatically. Continue?", "Restore Backup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunBusyAsync("RestoreBackup", "Restoring backup...", async () =>
        {
            await _services.Backups.RestoreBackupAsync(profile, picker.FileName);
            OperationText.Text = "Backup restored successfully.";
        }, profile);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile) return;
        var picker = new SaveFileDialog
        {
            Filter = "Palworld Server Package (*.palserver)|*.palserver",
            FileName = SanitizeFileName(profile.Name) + "_" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".palserver"
        };
        if (picker.ShowDialog(this) != true) return;
        await RunBusyAsync("ExportPortablePackage", "Exporting server...", async () =>
        {
            await _services.Packages.ExportAsync(profile, picker.FileName);
            OperationText.Text = "Export completed: " + picker.FileName;
        }, profile);
    }

    private async void SendToPc_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile) return;
        new SendServerWindow(_services, profile) { Owner = this }.ShowDialog();
        await RefreshProfilesAsync(profile.Id);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var profile = Selected;
        if (profile is null || !Directory.Exists(profile.InstallPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{profile.InstallPath}\"") { UseShellExecute = true });
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_services.Logger.LogsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_services.Logger.LogsDirectory}\"") { UseShellExecute = true });
        _services.Logger.Info("Opened manager logs folder from UI.");
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var profile = Selected;
        var baseName = profile is null ? "PalworldServerManager" : SanitizeFileName(profile.Name);
        var picker = new SaveFileDialog
        {
            Filter = "Diagnostic ZIP (*.zip)|*.zip",
            FileName = $"{baseName}_diagnostics_{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (picker.ShowDialog(this) != true) return;

        await RunBusyAsync("ExportDiagnostics", "Creating diagnostic bundle...", async () =>
        {
            var output = await _services.Diagnostics.CreateAsync(picker.FileName, profile);
            OperationText.Text = "Diagnostic bundle created: " + output;
            MessageBox.Show(this,
                "Diagnostic bundle created. It includes manager logs, recent Palworld server logs, environment information, and sanitized settings. World save files are not included and server/admin passwords are redacted.",
                "Diagnostics Ready", MessageBoxButton.OK, MessageBoxImage.Information);
        }, profile);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshProfilesAsync(Selected?.Id);
    private void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshSelectedDetails();

    private void OpenUpdates_Click(object sender, RoutedEventArgs e) => new UpdatesWindow(_services) { Owner = this }.ShowDialog();
    private void OpenDocumentation_Click(object sender, RoutedEventArgs e) => DocumentationLinks.Open(DocumentationLinks.Home, this);
    private void OpenTroubleshooting_Click(object sender, RoutedEventArgs e) => DocumentationLinks.Open(DocumentationLinks.Troubleshooting, this);
    private void OpenReportBug_Click(object sender, RoutedEventArgs e) => DocumentationLinks.Open(DocumentationLinks.ReportBug, this);

    private static string SanitizeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return value;
    }
}
