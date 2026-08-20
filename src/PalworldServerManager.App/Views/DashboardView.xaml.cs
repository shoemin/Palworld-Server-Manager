using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PalworldServerManager.Core.Models;
using PalworldServerManager.Lan;

namespace PalworldServerManager.App.Views;

public partial class DashboardView : UserControl
{
    private readonly DispatcherTimer _timer;
    private readonly List<double> _fpsHistory = [];
    private readonly List<double> _playerHistory = [];
    private readonly List<double> _frameHistory = [];
    private bool _refreshing;
    private string? _historyKey;

    public DashboardView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += async (_, _) => await RefreshDashboardAsync();
        Loaded += async (_, _) =>
        {
            await RefreshSourcesAsync();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private AppServices Services => App.Services;

    private async Task RefreshSourcesAsync()
    {
        var selectedKey = (SourceCombo.SelectedItem as DashboardSource)?.Key;
        var sources = new List<DashboardSource>();

        foreach (var profile in await Services.Registry.LoadAsync())
            sources.Add(DashboardSource.Local(profile));

        if (Services.Lan.Running)
        {
            foreach (var peer in Services.Lan.GetPeers().Where(x => x.IsPaired))
            {
                try
                {
                    var servers = await Services.Lan.Client.GetServersAsync(peer);
                    sources.AddRange(servers.Select(server => DashboardSource.Remote(peer, server)));
                }
                catch (Exception ex)
                {
                    Services.Logger.Warning($"Could not enumerate remote dashboard servers from '{peer.MachineName}': {ex.Message}");
                }
            }
        }

        SourceCombo.ItemsSource = sources;
        SourceCombo.DisplayMemberPath = nameof(DashboardSource.DisplayName);
        SourceCombo.SelectedItem = sources.FirstOrDefault(x => x.Key == selectedKey) ?? sources.FirstOrDefault();
        if (SourceCombo.SelectedItem is null) ClearDashboard("No local or paired remote servers are available.");
    }

    private async Task RefreshDashboardAsync()
    {
        if (_refreshing || SourceCombo.SelectedItem is not DashboardSource source) return;
        _refreshing = true;
        try
        {
            ConnectionText.Text = $"Refreshing {source.DisplayName}...";
            DashboardSnapshot snapshot;
            if (source.IsLocal)
            {
                var profile = (await Services.Registry.LoadAsync()).FirstOrDefault(x => x.Id == source.ProfileId)
                    ?? throw new InvalidOperationException("Selected local server no longer exists.");
                snapshot = await Services.Dashboard.GetSnapshotAsync(profile);
            }
            else
            {
                snapshot = await Services.Lan.Client.GetDashboardAsync(source.Peer!, source.ProfileId);
            }

            ApplySnapshot(snapshot, source);
        }
        catch (Exception ex)
        {
            ConnectionText.Text = "Dashboard unavailable.";
            DashboardErrorText.Text = ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplySnapshot(DashboardSnapshot snapshot, DashboardSource source)
    {
        ConnectionText.Text = $"{(source.IsLocal ? "Local" : "LAN")} • {snapshot.SourceMachine} • {snapshot.ManagerStatus}";
        ManagerStatusText.Text = snapshot.ManagerStatus;
        RestStatusText.Text = snapshot.RestAvailable ? "Connected" : snapshot.RestConfigured ? "Unavailable" : "Not configured";
        LiveServerNameText.Text = snapshot.Info?.ServerName ?? snapshot.ProfileName;
        ServerVersionText.Text = snapshot.Info?.Version ?? "—";
        WorldGuidText.Text = snapshot.Info?.WorldGuid ?? "—";
        UptimeText.Text = FormatUptime(snapshot.Metrics?.UptimeSeconds);
        DaysText.Text = snapshot.Metrics?.Days.ToString() ?? "—";
        PlayerCountText.Text = snapshot.Metrics is null ? snapshot.Players.Count.ToString() : $"{snapshot.Metrics.CurrentPlayerNum} / {snapshot.Metrics.MaxPlayerNum}";
        BaseCampText.Text = snapshot.Metrics?.BaseCampNum.ToString() ?? "—";
        FpsText.Text = snapshot.Metrics?.ServerFps.ToString() ?? "—";
        FrameTimeText.Text = snapshot.Metrics is null ? "—" : $"{snapshot.Metrics.ServerFrameTime:F2} ms";
        GamePortText.Text = snapshot.GamePort.ToString();
        RestPortText.Text = snapshot.RestPort.ToString();
        LastBackupText.Text = snapshot.LastBackupUtc?.ToLocalTime().ToString("g") ?? "No Manager backup found";
        DashboardErrorText.Text = snapshot.RestError ?? string.Empty;

        PlayersGrid.ItemsSource = snapshot.Players;
        SettingsGrid.ItemsSource = snapshot.Settings;

        if (_historyKey != source.Key)
        {
            _historyKey = source.Key;
            _fpsHistory.Clear();
            _playerHistory.Clear();
            _frameHistory.Clear();
        }

        if (snapshot.Metrics is not null)
        {
            AddHistory(_fpsHistory, snapshot.Metrics.ServerFps);
            AddHistory(_playerHistory, snapshot.Metrics.CurrentPlayerNum);
            AddHistory(_frameHistory, snapshot.Metrics.ServerFrameTime);
        }
        DrawGraphs();
    }

    private static void AddHistory(List<double> values, double value)
    {
        values.Add(value);
        while (values.Count > 720) values.RemoveAt(0);
    }

    private void DrawGraphs()
    {
        DrawSeries(FpsCanvas, _fpsHistory, includeZero: false);
        DrawSeries(PlayersCanvas, _playerHistory, includeZero: true);
        DrawSeries(FrameTimeCanvas, _frameHistory, includeZero: true);
    }

    private static void DrawSeries(Canvas canvas, IReadOnlyList<double> values, bool includeZero)
    {
        canvas.Children.Clear();
        if (values.Count < 2 || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;

        var min = includeZero ? 0 : values.Min();
        var max = values.Max();
        if (Math.Abs(max - min) < 0.0001) max = min + 1;

        var points = new PointCollection(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var x = i * canvas.ActualWidth / Math.Max(1, values.Count - 1);
            var normalized = (values[i] - min) / (max - min);
            var y = canvas.ActualHeight - normalized * canvas.ActualHeight;
            points.Add(new Point(x, y));
        }

        canvas.Children.Add(new Polyline
        {
            Points = points,
            Stroke = SystemColors.HighlightBrush,
            StrokeThickness = 2
        });
    }

    private void ClearDashboard(string message)
    {
        ConnectionText.Text = message;
        ManagerStatusText.Text = "—";
        RestStatusText.Text = "—";
        LiveServerNameText.Text = "—";
        ServerVersionText.Text = "—";
        WorldGuidText.Text = "—";
        UptimeText.Text = "—";
        DaysText.Text = "—";
        PlayerCountText.Text = "—";
        BaseCampText.Text = "—";
        FpsText.Text = "—";
        FrameTimeText.Text = "—";
        GamePortText.Text = "—";
        RestPortText.Text = "—";
        LastBackupText.Text = "—";
        DashboardErrorText.Text = string.Empty;
        PlayersGrid.ItemsSource = null;
        SettingsGrid.ItemsSource = null;
    }

    private static string FormatUptime(long? seconds)
    {
        if (seconds is null) return "—";
        var span = TimeSpan.FromSeconds(seconds.Value);
        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d {span.Hours:00}h {span.Minutes:00}m"
            : $"{span.Hours:00}h {span.Minutes:00}m {span.Seconds:00}s";
    }

    private async void RefreshSources_Click(object sender, RoutedEventArgs e) => await RefreshSourcesAsync();
    private async void RefreshNow_Click(object sender, RoutedEventArgs e) => await RefreshDashboardAsync();
    private async void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => await RefreshDashboardAsync();
    private void MetricCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawGraphs();

    private void AdvancedPlayersCheck_Changed(object sender, RoutedEventArgs e)
    {
        var visibility = AdvancedPlayersCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AccountColumn.Visibility = visibility;
        UserIdColumn.Visibility = visibility;
        PlayerIdColumn.Visibility = visibility;
        IpColumn.Visibility = visibility;
    }

    private sealed class DashboardSource
    {
        public string Key { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public bool IsLocal { get; init; }
        public Guid ProfileId { get; init; }
        public LanPeer? Peer { get; init; }

        public static DashboardSource Local(ServerProfile profile) => new()
        {
            Key = $"local:{profile.Id:D}",
            DisplayName = $"LOCAL / {Environment.MachineName} / {profile.Name}",
            IsLocal = true,
            ProfileId = profile.Id
        };

        public static DashboardSource Remote(LanPeer peer, RemoteServerSummary server) => new()
        {
            Key = $"remote:{peer.InstanceId:D}:{server.Id:D}",
            DisplayName = $"LAN / {peer.MachineName} / {server.Name}",
            IsLocal = false,
            ProfileId = server.Id,
            Peer = peer
        };
    }
}
