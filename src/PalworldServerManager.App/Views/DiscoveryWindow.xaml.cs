using System.Windows;
using PalworldServerManager.App.Services;
using Microsoft.Win32;
using PalworldServerManager.Core.Models;
using PalworldServerManager.Core.Services;

namespace PalworldServerManager.App.Views;

public partial class DiscoveryWindow : Window
{
    private readonly AppServices _services;
    private bool _busy;
    public Guid? LastImportedId { get; private set; }

    public DiscoveryWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        Loaded += async (_, _) => await ScanAsync();
    }

    private async Task ScanAsync()
    {
        if (_busy) return;
        _busy = true;
        ProgressText.Text = "Checking expected Steam and SteamCMD locations...";
        using var operation = _services.Logger.BeginOperation("ScanExistingServers");
        try
        {
            var results = await _services.Discovery.ScanExpectedLocationsAsync();
            _services.Logger.Info($"Existing-server scan returned {results.Count} candidate(s).");
            ResultsGrid.ItemsSource = results;
            ProgressText.Text = results.Count == 0 ? "No Palworld dedicated servers were found in expected locations." : $"Found {results.Count} candidate installation(s).";
        }
        catch (Exception ex)
        {
            _services.Logger.Error("Existing-server scan failed.", ex);
            ProgressText.Text = "Scan failed: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Discovery", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void Manual_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Select the directory containing PalServer.exe",
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;

        using var operation = _services.Logger.BeginOperation("AnalyzeManualExistingServer");
        var profiles = await _services.Registry.LoadAsync();
        var candidate = _services.Discovery.Analyze(picker.FolderName, profiles);
        _services.Logger.Info($"Manual candidate analyzed. Path='{picker.FolderName}' Classification={candidate.Classification} HasSave={candidate.HasSaveData} HasSettings={candidate.HasSettings} Running={candidate.IsRunning}");
        ResultsGrid.ItemsSource = new[] { candidate };
        ResultsGrid.SelectedItem = candidate;
        ProgressText.Text = candidate.Notes;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ResultsGrid.SelectedItem is not ExistingServerCandidate candidate) return;
        if (candidate.IsAlreadyManaged)
        {
            MessageBox.Show(this, "This server has already been imported or is already manager-owned.");
            return;
        }
        if (candidate.IsRunning)
        {
            MessageBox.Show(this, "Stop the source Palworld server before importing it. The manager intentionally refuses to copy a live world.", "Server Running", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (candidate.Classification is ExistingServerClassification.Invalid or ExistingServerClassification.PossibleServer)
        {
            MessageBox.Show(this, "This directory does not match a sufficiently complete Palworld dedicated-server installation.");
            return;
        }

        var answer = MessageBox.Show(this,
            $"Import '{candidate.DisplayName}' as a new managed copy?\n\nSource:\n{candidate.Path}\n\nThe original source is read only and will be hashed before and after import.",
            "Import Existing Server", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        if (!SteamUiRecovery.ConfirmPreflight(this, _services))
        {
            ProgressText.Text = "Import canceled before SteamCMD provisioning.";
            return;
        }

        _busy = true;
        IsEnabled = false;
        using var operation = _services.Logger.BeginOperation("ImportExistingServer", serverName: candidate.DisplayName);
        _services.Logger.Info($"Import requested from source '{candidate.Path}'.");
        try
        {
            var progress = new Progress<string>(s => { ProgressText.Text = s; _services.Logger.Debug("Import progress: " + s); });
            var recoveryRetries = 0;
            while (true)
            {
                try
                {
                    var profile = await _services.ExistingImport.ImportAsync(candidate.Path, progress);
                    LastImportedId = profile.Id;
                    ProgressText.Text = "Import completed. Original source data verified unchanged.";
                    MessageBox.Show(this, $"Imported as '{profile.Name}'. The original server was left untouched.", "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                }
                catch (SteamCmdException ex) when (ex.SuggestSteamClientRecovery && recoveryRetries == 0)
                {
                    _services.Logger.Warning($"Existing-server import encountered SteamCMD exit code {ex.ExitCode}; offering one interactive recovery retry.");
                    IsEnabled = true;
                    var retry = SteamUiRecovery.PromptRetryAfterFailure(this, _services, ex);
                    IsEnabled = false;
                    if (!retry) throw;
                    recoveryRetries++;
                    ProgressText.Text = "Retrying import after Steam preflight...";
                }
            }
            await ScanAsyncAfterBusy();
        }
        catch (Exception ex)
        {
            _services.Logger.Error("Existing-server import failed.", ex);
            ProgressText.Text = "Import failed: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Import Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
            _busy = false;
        }
    }

    private async Task ScanAsyncAfterBusy()
    {
        var results = await _services.Discovery.ScanExpectedLocationsAsync();
        ResultsGrid.ItemsSource = results;
    }
}
