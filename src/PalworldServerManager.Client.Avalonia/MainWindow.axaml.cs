using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PalworldServerManager.Client.Avalonia.Shell;
using PalworldServerManager.Client.Avalonia.Views;
using PalworldServerManager.Contracts;
using PalworldServerManager.Client.Security;

namespace PalworldServerManager.Client.Avalonia;

public partial class MainWindow : Window
{
    private readonly Grid _body = new(), _railGrid = new() { RowDefinitions = new("Auto,*,Auto") };
    private readonly Grid _chrome = new();
    private readonly TextBlock _brand = Text("PALWORLD / SERVER MANAGER");
    private readonly WrapPanel _globalActions = new();
    private readonly Border _headingCard = new();
    private readonly AngularCard _headingFrame = new() { IsHitTestVisible = false };
    private readonly TextBlock _addReason = Text("Creation and import unavailable.", "muted");
    private readonly Border _rail = new(), _drawer = new(), _identity = new();
    private readonly StackPanel _workspace = new() { Spacing = 16 }, _panel = new() { Spacing = 16 };
    private readonly ServerTree _tree = new() { Name = "ServerTree", AutoScrollToSelectedItem = false };
    private readonly Button _expand, _all, _thisPc, _details, _add;
    private readonly TextBlock _title = Text("All Servers"), _host = Text("Local Host disconnected", "accent"), _status = Text("Local Host disconnected", "muted");
    private ShellState? _state;
    private IReadOnlyList<ShellRow>? _rows;
    private HostId? _scope;
    private readonly Dictionary<ServerRef, TreeViewItem> _nodes = [];
    private readonly HashSet<HostId> _collapsedGroups = [];
    private Control? _panelOpener;
    private bool _compact, _railOpen, _manualCollapse, _syncSelection, _suppressInspection;
    private string? _panelKind;
    private long _selectionVersion;
    private CancellationTokenSource _selectionStop = new();
    private bool _closed;
    public ShellTheme CurrentTheme { get; private set; }
    public bool ReduceMotion { get; private set; } = true;
    public ShellState? State => _state;
    private static TextBlock Text(string value, string? style = null)
    { var text = new TextBlock { Text = value }; if (style is not null) text.Classes.Add(style); return text; }
    private static Button Action(string label, string name, Action<Button> click)
    {
        var button = new Button { Name = name, Content = Text(label), HorizontalContentAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(button, label); button.Click += (_, _) => click(button); return button;
    }
    private static Border Card(Control child, double padding = 16)
    { var border = new Border { Child = child, Padding = new(padding) }; border.Classes.Add("card"); return border; }
    public MainWindow() : this(null) { }
    public MainWindow(Func<CancellationToken, Task<LocalConnectionInfo>>? connectLocal)
    {
        _connectLocal = connectLocal;
        InitializeComponent(); Resources["BodySize"] = 14d; SetTheme(ShellTheme.Refined);
        _chrome.Margin = new(16, 8); _brand.Margin = new(4, 12, 24, 12); _chrome.Children.Add(_brand); _chrome.Children.Add(_globalActions);
        foreach (var label in new[] { "Activity", "Alerts", "Manager Settings" })
        { var button = Action(label, label.Replace(" ", ""), opener => OpenPanel(label, opener)); button.Margin = new(4, 0, 4, 4); _globalActions.Children.Add(button); }
        Root.Children.Add(Card(_chrome, 0));
        Grid.SetRow(_body, 1); Root.Children.Add(_body);
        _rail.Name = "ServerRail"; _rail.Child = _railGrid; _rail.Classes.Add("card"); _rail.Padding = new(8); _body.Children.Add(_rail);
        var scopeControls = new StackPanel { Spacing = 8 };
        _all = Action("All Servers", "AllServers", _ => _state?.Show(null));
        _thisPc = Action("This PC", "ThisPC", _ => { if (_state is { } state) state.Show(state.LocalHost); });
        scopeControls.Children.Add(_all); scopeControls.Children.Add(_thisPc); _railGrid.Children.Add(scopeControls);
        Grid.SetRow(_tree, 1); _tree.Margin = new(0, 16); _railGrid.Children.Add(_tree); AutomationProperties.SetName(_tree, "Authorized servers by Host");
        ScrollViewer.SetHorizontalScrollBarVisibility(_tree, global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        var foot = new StackPanel { Spacing = 8 };
        _add = Action("Add / Import", "AddImport", _ => { }); _add.IsEnabled = false; foot.Children.Add(_add);
        foot.Children.Add(_addReason);
        _expand = Action("Collapse server rail", "ExpandRail", _ => ToggleRail()); foot.Children.Add(_expand);
        Grid.SetRow(foot, 2); _railGrid.Children.Add(foot);
        var workspaceScroll = new ScrollViewer { Content = _workspace, Margin = new(24, 24, 24, 16), Name = "Workspace" };
        Grid.SetColumn(workspaceScroll, 1); _body.Children.Add(workspaceScroll);
        _workspace.Children.Add(Text("SERVER WORKSPACE", "accent"));
        var heading = new StackPanel { Spacing = 12 }; _title.FontSize = 28; heading.Children.Add(_title); heading.Children.Add(_host);
        _details = Action("Identity details", "IdentityDetails", opener => ShowIdentity(_state?.SelectedRow, opener)); heading.Children.Add(_details);
        _headingCard.Child = heading; _headingCard.Padding = new(20); _headingCard.Classes.Add("card"); var headingPanel = new Grid(); headingPanel.Children.Add(_headingCard); headingPanel.Children.Add(_headingFrame); _workspace.Children.Add(headingPanel);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var label in new[] { "Overview", "Players", "Metrics", "Settings", "Backups" })
        { var tab = Action(label, "Tab" + label, _ => { }); tab.IsEnabled = false; tab.Margin = new(0, 0, 8, 8); tabs.Children.Add(tab); }
        var tabScroll = new ScrollViewer { Content = tabs, Name = "ServerTabs", HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var left = Action("‹", "TabsLeft", _ => tabScroll.Offset = new(Math.Max(0, tabScroll.Offset.X - 160), 0)); AutomationProperties.SetName(left, "Scroll server tabs left");
        var right = Action("›", "TabsRight", _ => tabScroll.Offset = new(tabScroll.Offset.X + 160, 0)); AutomationProperties.SetName(right, "Scroll server tabs right");
        var tabRegion = new Grid { ColumnDefinitions = new("Auto,*,Auto") }; tabRegion.Children.Add(left); Grid.SetColumn(tabScroll, 1); tabRegion.Children.Add(tabScroll); Grid.SetColumn(right, 2); tabRegion.Children.Add(right);
        tabScroll.PropertyChanged += (_, change) => { if (change.Property == ScrollViewer.ExtentProperty || change.Property == ScrollViewer.ViewportProperty)
            left.IsVisible = right.IsVisible = tabScroll.Extent.Width > tabScroll.Viewport.Width + 1; };
        _workspace.Children.Add(tabRegion);
        _workspace.Children.Add(CreateConnectionControls());
        _workspace.Children.Add(Card(Text("Server details, creation, import and other actions are unavailable until the Host supplies supported, authorized data.", "muted"), 24));
        _drawer.Name = "GlobalPanel"; _drawer.Classes.Add("card"); _drawer.Child = new ScrollViewer { Content = _panel }; _drawer.Padding = new(16); _drawer.Margin = new(8, 16, 16, 16); _drawer.IsVisible = false;
        _body.Children.Add(_drawer);
        _identity.Name = "IdentityPanel"; _identity.Classes.Add("card"); _identity.Classes.Add("frame"); _identity.Padding = new(16); _identity.IsVisible = false;
        _identity.HorizontalAlignment = HorizontalAlignment.Left; _identity.VerticalAlignment = VerticalAlignment.Top; _identity.MaxWidth = 480;
        Grid.SetColumn(_identity, 1); _body.Children.Add(_identity);
        _status.Margin = new(16, 8); Grid.SetRow(_status, 2); Root.Children.Add(_status);
        _tree.Inspect += node =>
        {
            foreach (var item in _nodes.Values) ((Border)item.Header!).Classes.Set("inspected", item == node);
            if (node.Tag is ShellRow row) { _state?.Focus(row.Reference); if (!_suppressInspection && _compact && !_railOpen) ShowIdentity(row, node, false); }
            else { _state?.Focus(null); CloseIdentity(); }
        };
        _tree.Activate += node => { if (node.Tag is ShellRow row) _ = Select(row.Reference); else node.IsExpanded = !node.IsExpanded; };
        _tree.SelectionChanged += (_, _) =>
        {
            if (!_syncSelection && _tree.SelectedItem is TreeViewItem { Tag: ShellRow row })
            { RefreshSelection(); _ = Select(row.Reference); }
        };
        SizeChanged += (_, _) => LayoutShell();
        // Fluent places a few transitions as local values inside scrollbar templates.
        // Keep this shell static, including newly materialized template parts, before rendering.
        LayoutUpdated += (_, _) => { foreach (var control in this.GetVisualDescendants().OfType<Control>()) if (control.Transitions is not null) control.Transitions = null; };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { if (_identity.IsVisible) CloseIdentity(); else if (_panelKind is not null) ClosePanel(); else if (_railOpen) ToggleRail(); else return; e.Handled = true; } };
        Closed += (_, _) => { _closed = true; _connectionStop?.Cancel(); _selectionVersion++; _selectionStop.Cancel(); _selectionStop.Dispose(); if (_state is not null) _state.PropertyChanged -= StateChanged; };
        LayoutShell(); Refresh();
    }
    public void SetTheme(ShellTheme theme)
    {
        foreach (var pair in ShellTokens.Accepted.Palette(theme)) Resources[pair.Key] = new SolidColorBrush(Color.Parse(pair.Value));
        CurrentTheme = theme;
        _headingFrame.Accent = theme == ShellTheme.Refined ? (IBrush)Resources["accent"]! : null; _headingFrame.InvalidateVisual();
        Resources["SystemControlHighlightAccentBrush"] = Resources["accent"];
        Resources["TextControlSelectionHighlightColor"] = Color.Parse(ShellTokens.Accepted.Palette(theme)["selected"]);
        // Theme variants change tokens, never the shell tree or target.
        RequestedThemeVariant = theme == ShellTheme.LightMinimal ? global::Avalonia.Styling.ThemeVariant.Light : global::Avalonia.Styling.ThemeVariant.Dark;
    }
    public void BindState(ShellState? state)
    {
        _selectionVersion++; _selectionStop.Cancel(); _selectionStop.Dispose(); _selectionStop = new();
        if (_state is not null) _state.PropertyChanged -= StateChanged;
        _state = state; _rows = null; _collapsedGroups.Clear(); if (state is not null) state.PropertyChanged += StateChanged;
        RebuildTree(); Refresh();
    }
    private void StateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();
    private void Refresh()
    {
        if (!ReferenceEquals(_rows, _state?.Rows) || _scope != _state?.Scope) RebuildTree();
        _title.Text = _state?.SelectedRow?.Name ?? (_state?.Scope is null ? "All Servers" : _state.Scope == _state.LocalHost ? "This PC" : "Remote Host");
        _host.Text = _state?.SelectedRow?.HostLabel ?? (VerifiedConnection is not null ? "This PC · identity verified; inventory unavailable" : _state is null ? "Local Host disconnected" : "Only authorized servers are shown");
        _details.IsVisible = _state?.SelectedRow is not null;
        _thisPc.IsEnabled = _state is not null;
        _status.Text = _state?.IsSelectionPending == true ? "Checking server access…" : VerifiedConnection is not null ? "Host verified on this request · inventory unavailable" : _state is null ? "Local Host disconnected" : "Authorized inventory · exact Host and server identity";
        RefreshSelection();
    }
    private void RefreshSelection()
    {
        _syncSelection = true;
        try
        {
            _tree.SelectedItem = _state?.Selected is { } selected && _nodes.TryGetValue(selected, out var selectedNode) ? selectedNode : null;
            foreach (var pair in _nodes) ((Border)pair.Value.Header!).Classes.Set("chosen", pair.Key == _state?.Selected);
        }
        finally { _syncSelection = false; }
    }
    private void RebuildTree()
    {
        var focus = _tree.Inspected?.Tag; var hadFocus = _tree.IsKeyboardFocusWithin;
        _syncSelection = true; _tree.Items.Clear(); _nodes.Clear(); _syncSelection = false;
        _rows = _state?.Rows; _scope = _state?.Scope; CloseIdentity();
        if (_state is null) { if (hadFocus) _all.Focus(); return; }
        var small = _compact && !_railOpen;
        foreach (var group in _state.VisibleRows.GroupBy(row => row.Reference.AuthoritativeHostId))
        {
            var first = group.First(); var expanded = !_collapsedGroups.Contains(group.Key);
            var groupLabel = Text((expanded ? "− " : "+ ") + (small ? first.HostAlias : first.HostLabel), "muted"); groupLabel.Width = small ? 64 : 244;
            var nodeTheme = (global::Avalonia.Styling.ControlTheme)Resources["ServerNodeTheme"]!;
            var groupNode = new TreeViewItem { Header = groupLabel, Tag = group.Key, IsExpanded = expanded, Theme = nodeTheme };
            groupNode.PropertyChanged += (_, change) => { if (change.Property == TreeViewItem.IsExpandedProperty)
                { groupLabel.Text = (groupNode.IsExpanded ? "− " : "+ ") + (small ? first.HostAlias : first.HostLabel); if (groupNode.IsExpanded) _collapsedGroups.Remove(group.Key); else _collapsedGroups.Add(group.Key); } };
            AutomationProperties.SetName(groupNode, first.HostLabel + " " + group.Key.Value.ToString("D"));
            foreach (var row in group)
            {
                var labels = new StackPanel { Spacing = 4 }; labels.Children.Add(Text(small ? row.ServerAlias : row.Name));
                if (!small) labels.Children.Add(Text(row.HostAlias + " · " + row.HostDiscriminator, "muted"));
                var header = new Border { Child = labels, Width = small ? 64 : 244 }; header.Classes.Add("row"); if (small) header.Padding = new(4, 8);
                var node = new TreeViewItem { Header = header, Tag = row, Theme = nodeTheme }; AutomationProperties.SetName(node, row.AccessibleName);
                node.PointerEntered += (_, _) => { if (_compact && !_railOpen) ShowIdentity(row, node, false); };
                node.LostFocus += (_, _) => header.Classes.Remove("inspected");
                groupNode.Items.Add(node); _nodes.Add(row.Reference, node);
            }
            _tree.Items.Add(groupNode);
        }
        var restored = focus is ShellRow focusedRow && _nodes.TryGetValue(focusedRow.Reference, out var serverNode) ? serverNode :
            focus is HostId focusedHost ? _tree.Items.OfType<TreeViewItem>().FirstOrDefault(item => item.Tag is HostId host && host == focusedHost) : null;
        var inventory = _rows;
        if (hadFocus && restored is not null) Dispatcher.UIThread.Post(() => { if (!_closed && ReferenceEquals(inventory, _rows) && _panelKind is null) restored.Focus(); });
        else if (hadFocus) _all.Focus();
    }
    private async Task Select(ServerRef target)
    {
        if (_state is not { } state) return;
        var version = ++_selectionVersion;
        bool Current() => !_closed && version == _selectionVersion && ReferenceEquals(state, _state);
        try { if (!await state.SelectAsync(target, _selectionStop.Token) && Current()) _status.Text = "Server access is no longer available."; }
        catch (Exception) { if (Current()) _status.Text = "Server access could not be verified. Reconnect and try again."; }
    }
    private void LayoutShell()
    {
        var layout = ShellTokens.Accepted.Layout(Math.Max(1, ClientSize.Width > 0 ? ClientSize.Width : Width));
        var compact = layout.IsCompact || _manualCollapse; var changed = _compact != compact; _compact = compact;
        if (!compact) _railOpen = false;
        var stackedChrome = ClientSize.Width < 1100 || FontSize > 20;
        _chrome.ColumnDefinitions = new(stackedChrome ? "*" : "*,Auto"); _chrome.RowDefinitions = new(stackedChrome ? "Auto,Auto" : "Auto");
        Grid.SetColumn(_globalActions, stackedChrome ? 0 : 1); Grid.SetRow(_globalActions, stackedChrome ? 1 : 0);
        var railWidth = compact ? 88 : 280; var dock = _panelKind is not null && !layout.DrawerOverlays;
        _body.ColumnDefinitions = new ColumnDefinitions($"{railWidth},*,{(dock ? 328 : 0)}");
        _rail.Width = compact && _railOpen ? 280 : railWidth; _rail.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumnSpan(_rail, compact && _railOpen ? 2 : 1); _rail.ZIndex = _railOpen ? 2 : 0;
        Grid.SetColumn(_drawer, dock ? 2 : 1); Grid.SetColumnSpan(_drawer, dock ? 1 : 2); _drawer.Width = 304; _drawer.HorizontalAlignment = HorizontalAlignment.Right; _drawer.ZIndex = 3; _identity.ZIndex = 4;
        _identity.MaxWidth = Math.Max(100, Math.Min(480, Math.Max(1, ClientSize.Width) - railWidth - 16));
        var small = compact && !_railOpen;
        _all.Content = Text(small ? "All" : "All Servers"); _thisPc.Content = Text(small ? "L" : "This PC"); _add.Content = Text(small ? "+" : "Add / Import"); _addReason.IsVisible = !small;
        _expand.Content = Text(small ? "»" : "Collapse server rail");
        AutomationProperties.SetName(_expand, compact && !_railOpen ? "Expand server rail" : "Collapse server rail");
        if (changed) { RebuildTree(); RefreshSelection(); }
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontSizeProperty)
        { Resources["BodySize"] = FontSize; _title.FontSize = FontSize * 2; if (_expand is not null) LayoutShell(); }
    }
    private void ToggleRail()
    {
        if (!_compact) { _manualCollapse = true; _railOpen = false; }
        else if (_railOpen) _railOpen = false;
        else if (_manualCollapse && ClientSize.Width >= 1200) _manualCollapse = false;
        else _railOpen = true;
        LayoutShell(); RebuildTree(); RefreshSelection(); _expand.Focus();
    }
    private Control? _identityOpener;
    private void ShowIdentity(ShellRow? row, Control opener, bool moveFocus = true)
    {
        if (row is null) return;
        _identityOpener = opener; var content = new StackPanel { Spacing = 12 };
        var value = new SelectableTextBlock { Text = row.IdentityDetails, TextWrapping = TextWrapping.Wrap, Name = "FullIdentity" }; AutomationProperties.SetName(value, row.AccessibleName);
        content.Children.Add(value); var close = Action("Close identity", "CloseIdentity", _ => CloseIdentity()); content.Children.Add(close);
        _identity.Child = content; _identity.IsVisible = true; if (moveFocus) close.Focus();
    }
    private void CloseIdentity()
    {
        var returnFocus = _identity.IsKeyboardFocusWithin; _identity.IsVisible = false; _identity.Child = null;
        _suppressInspection = true; try { if (returnFocus) _identityOpener?.Focus(); } finally { _suppressInspection = false; _identityOpener = null; }
    }
    private void OpenPanel(string kind, Control opener)
    {
        CloseIdentity(); _panelKind = kind; _panelOpener = opener; _panel.Children.Clear();
        _panel.Children.Add(Text(kind, "accent")); var close = Action("Close " + kind, "ClosePanel", _ => ClosePanel()); _panel.Children.Add(close);
        if (kind == "Manager Settings")
        {
            _panel.Children.Add(Text("Appearance"));
            foreach (var pair in new[] { (ShellTheme.Refined, "Palworld Refined Desktop"), (ShellTheme.DarkMinimal, "Dark Minimal"), (ShellTheme.LightMinimal, "Light Minimal") })
                _panel.Children.Add(Action(pair.Item2, "Theme" + pair.Item1, _ => SetTheme(pair.Item1)));
            var motion = new CheckBox { Content = "Reduce motion", IsChecked = ReduceMotion, MinHeight = 40 };
            motion.IsCheckedChanged += (_, _) => ReduceMotion = motion.IsChecked == true; _panel.Children.Add(motion);
            _panel.Children.Add(Text("Other Manager settings are unavailable.", "muted"));
        }
        else _panel.Children.Add(Text(kind + " is unavailable until the Host supplies authorized data.", "muted"));
        _drawer.IsVisible = true; LayoutShell(); close.Focus();
    }
    private void ClosePanel() { _panelKind = null; _drawer.IsVisible = false; _panel.Children.Clear(); LayoutShell(); _panelOpener?.Focus(); _panelOpener = null; }
}
