using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using PalworldServerManager.Core.Models;

namespace PalworldServerManager.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppServices _services;
    private readonly ServerProfile _profile;
    private ObservableCollection<SettingEditorItem> _items = [];
    private ICollectionView? _view;

    public SettingsWindow(AppServices services, ServerProfile profile)
    {
        InitializeComponent();
        _services = services;
        _profile = profile;
        TitleText.Text = $"Settings — {profile.Name}";
        ProfileNameBox.Text = profile.Name;
        GamePortBox.Text = profile.GamePort.ToString();
        LaunchArgsBox.Text = profile.AdditionalLaunchArguments;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        using var operation = _services.Logger.BeginOperation("LoadSettings", _profile.Id, _profile.Name);
        _items = new ObservableCollection<SettingEditorItem>(await _services.Settings.LoadForEditingAsync(_profile));
        SettingsGrid.ItemsSource = _items;
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = FilterItem;

        var previous = CategoryBox.SelectedItem as string;
        var categories = new[] { "All" }.Concat(_items.Select(x => x.Category).Distinct().OrderBy(x => x)).ToList();
        CategoryBox.ItemsSource = categories;
        CategoryBox.SelectedItem = previous is not null && categories.Contains(previous) ? previous : "All";
    }

    private bool FilterItem(object obj)
    {
        if (obj is not SettingEditorItem item) return false;
        var category = CategoryBox.SelectedItem as string ?? "All";
        if (category != "All" && !string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase)) return false;
        var search = SearchBox.Text.Trim();
        if (search.Length == 0) return true;
        return item.Key.Contains(search, StringComparison.OrdinalIgnoreCase)
               || item.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
               || item.Value.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _view?.Refresh();
    private void CategoryBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => _view?.Refresh();

    private void ResetSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsGrid.SelectedItem is not SettingEditorItem item || item.DefaultValue is null) return;
        item.Value = item.DefaultValue;
        SettingsGrid.Items.Refresh();
    }

    private void OpenRaw_Click(object sender, RoutedEventArgs e)
    {
        _services.Settings.EnsureActiveConfig(_profile);
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_profile.SettingsPath}\"") { UseShellExecute = true });
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        using var operation = _services.Logger.BeginOperation("SaveSettings", _profile.Id, _profile.Name);
        try
        {
            SettingsGrid.CommitEdit();
            if (string.IsNullOrWhiteSpace(ProfileNameBox.Text)) throw new InvalidOperationException("Profile name cannot be blank.");
            if (!int.TryParse(GamePortBox.Text, out var gamePort) || gamePort is < 1 or > 65535) throw new InvalidOperationException("Game port must be between 1 and 65535.");
            _profile.Name = ProfileNameBox.Text.Trim();
            _profile.GamePort = gamePort;
            _profile.AdditionalLaunchArguments = LaunchArgsBox.Text.Trim();
            await _services.Settings.SaveAsync(_profile, _items);
            var restItem = _items.FirstOrDefault(x => x.Key.Equals("RESTAPIPort", StringComparison.OrdinalIgnoreCase));
            if (restItem is not null && int.TryParse(PalworldServerManager.Core.Services.PalworldConfigParser.Unquote(restItem.Value), out var restPort)) _profile.RestApiPort = restPort;
            var profiles = await _services.Registry.LoadAsync();
            var index = profiles.FindIndex(x => x.Id == _profile.Id);
            if (index >= 0) profiles[index] = _profile;
            await _services.Registry.SaveAsync(profiles);
            TitleText.Text = $"Settings — {_profile.Name}";
            _services.Logger.Info($"Settings/profile metadata saved for '{_profile.Name}'. GamePort={_profile.GamePort} AdditionalLaunchArgumentsPresent={!string.IsNullOrWhiteSpace(_profile.AdditionalLaunchArguments)}.");
            MessageBox.Show(this, "Settings saved. They will take effect the next time the server starts.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"Could not save settings for '{_profile.Name}'.", ex);
            MessageBox.Show(this, ex.Message, "Could Not Save Settings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
