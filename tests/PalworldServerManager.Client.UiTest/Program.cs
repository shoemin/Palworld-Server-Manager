using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PalworldServerManager.Client.Avalonia;
using PalworldServerManager.Client.Avalonia.Shell;
using PalworldServerManager.Client.Avalonia.Views;
using PalworldServerManager.Contracts;

namespace PalworldServerManager.Client.UiTest;

public static partial class Program
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    public static async Task<int> Main(string[] args)
    {
        using var deadline = new Timer(_ => { Console.Error.WriteLine("UI test process deadline exceeded."); Environment.Exit(2); }, null, TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
        var actual = args is ["--actual-local-connect"];
        using var session = HeadlessUnitTestSession.StartNew(typeof(Program));
        try
        {
            if (actual) return await session.Dispatch(ActualLocalConnection, CancellationToken.None);
            var output = args.Length == 0 ? Path.Combine("build-logs", "shell-renders") : args[0]; Directory.CreateDirectory(output);
            await session.Dispatch(() => { Run(output); ConnectionChecks(output); }, CancellationToken.None);
            Console.WriteLine("PASS actual Avalonia shell rendering/input and connection checks"); return 0;
        }
        catch (Exception failure) { Console.Error.WriteLine(actual ? "Actual UI connection fixture failed." : failure.ToString()); Environment.Exit(1); return 1; }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static T Find<T>(MainWindow window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(MainWindow window, string name)
    {
        window.UpdateLayout(); using var inputFrame = window.CaptureRenderedFrame(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var inputReady = window.CaptureRenderedFrame();
        var button = Find<Button>(window, name); var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static void Key(MainWindow window, PhysicalKey key)
    { window.KeyPressQwerty(key, RawInputModifiers.None); window.KeyReleaseQwerty(key, RawInputModifiers.None); }
    private static void Capture(MainWindow window, string output, string name)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        using var warmup = window.CaptureRenderedFrame(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame() ?? throw new Exception("Actual renderer produced no frame."); frame.Save(Path.Combine(output, name + ".png"));
        Check(frame.PixelSize.Width == (int)window.Width && frame.PixelSize.Height == (int)window.Height, "Rendered frame dimensions drifted.");
    }
    private static void Run(string output)
    {
        var local = new HostId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var remoteA = new HostId(Guid.Parse("aaaaaaaa-aaaa-aaaa-0000-ffffffffffff"));
        var remoteB = new HostId(Guid.Parse("aaaaaaaa-bbbb-bbbb-0000-ffffffffffff"));
        var profile = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"); ServerRef? requested = null;
        var state = new ShellState(local, (target, _) => { requested = target; return Task.FromResult(true); });
        var rows = new[] { local, remoteA, remoteB }.Select(host => new ShellServer(new(host, profile), "Main Server", "Family PC")).ToArray();
        state.ReplaceAuthorizedInventory(rows);
        var window = ((App)Application.Current!).CreateMainWindow();
        try
        {
            window.Show(); Capture(window, output, "disconnected");
            Check(window.State is null && Find<ServerTree>(window, "ServerTree").Items.Count == 0 && Find<Button>(window, "ConnectLocal").IsEnabled, "Production shell shipped synthetic inventory or lost connection wiring.");
            window.BindState(state); Dispatcher.UIThread.RunJobs();
            var tree = Find<ServerTree>(window, "ServerTree"); var groups = tree.Items.Cast<TreeViewItem>().ToArray();
            var first = (TreeViewItem)groups[0].Items[0]!; first.Focus(); Key(window, PhysicalKey.ArrowDown); Key(window, PhysicalKey.ArrowDown);
            Check(state.Selected is null && requested is null && state.Focused == rows[1].Reference, "Tree arrows selected instead of inspecting exact target.");
            Key(window, PhysicalKey.Enter); Check(state.Selected == rows[1].Reference && requested == rows[1].Reference, "Enter failed exact Host validation.");
            Check(AutomationProperties.GetName((TreeViewItem)groups[1].Items[0]!)!.Contains(remoteA.Value.ToString("D")), "Accessible row lost full identity.");
            Key(window, PhysicalKey.ArrowLeft); Key(window, PhysicalKey.ArrowLeft); Check(!groups[1].IsExpanded && state.Focused is null, "Group collapse retained a false server focus.");
            Key(window, PhysicalKey.ArrowRight); Check(groups[1].IsExpanded, "Right failed to expand Host group.");
            Key(window, PhysicalKey.End); Check(state.Focused == rows[2].Reference && state.Selected == rows[1].Reference, "End redirected selection.");
            var geometryByWidth = new Dictionary<int, Rect[]>();
            foreach (var theme in Enum.GetValues<ShellTheme>())
            foreach (var width in new[] { 1600, 2100, 800 })
            {
                window.SetTheme(theme); window.Width = width; Dispatcher.UIThread.RunJobs();
                Click(window, "Activity"); Check(Find<Border>(window, "GlobalPanel").IsVisible, "Activity did not open."); Capture(window, output, $"{theme}-{width}");
                var currentTree = Find<ServerTree>(window, "ServerTree");
                var geometry = currentTree.Items.Cast<TreeViewItem>().SelectMany(group => new[] { group }.Concat(group.Items.Cast<TreeViewItem>()))
                    .Select(node => new Rect(node.TranslatePoint(default, window)!.Value, node.Bounds.Size)).ToArray();
                if (geometryByWidth.TryGetValue(width, out var previous)) Check(previous.SequenceEqual(geometry), "Changing theme changed tree geometry.");
                else geometryByWidth.Add(width, geometry);
                var selectedControl = (TreeViewItem)currentTree.SelectedItem!;
                var selectedBorder = selectedControl.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_LayoutRoot");
                Check(selectedBorder.BorderBrush is ISolidColorBrush selectedBrush && selectedBrush.Color == Color.Parse(ShellTokens.Accepted.Palette(theme)["accent"]), "Selected row border did not use actual semantic accent.");
                Check(Find<Border>(window, "ServerRail").Bounds.Width == (width < 1200 ? 88 : 280), "Actual rail width differs from accepted geometry.");
                Check(Find<Border>(window, "GlobalPanel").Bounds.Width == 304, "Actual Activity drawer width drifted.");
                Key(window, PhysicalKey.Escape); Check(Find<Button>(window, "Activity").IsFocused && !Find<Border>(window, "GlobalPanel").IsVisible, "Panel did not return focus to opener.");
                Check(state.Selected == rows[1].Reference, "Theme/layout/panel changed exact target.");
            }
            window.Width = 1600; window.UpdateLayout(); Click(window, "ThisPC");
            Check(state.Scope == local && state.Selected is null && tree.Items.Count == 1, "This PC scope retained a remote target.");
            Click(window, "AllServers"); Check(state.Scope is null && tree.Items.Count == 3, "All Servers did not restore authorized groups.");
            var collapsedGroup = (TreeViewItem)tree.Items[2]!; collapsedGroup.Focus(); Key(window, PhysicalKey.ArrowLeft);
            window.Width = 800; window.UpdateLayout(); Check(!((TreeViewItem)tree.Items[2]!).IsExpanded, "Responsive rail rebuild discarded collapsed group.");
            ((TreeViewItem)tree.Items[2]!).IsExpanded = true;
            ((TreeViewItem)((TreeViewItem)tree.Items[1]!).Items[0]!).Focus(); Key(window, PhysicalKey.Enter);
            window.SetTheme(ShellTheme.Refined); window.Width = 800; Dispatcher.UIThread.RunJobs();
            tree = Find<ServerTree>(window, "ServerTree"); ((TreeViewItem)((TreeViewItem)tree.Items[2]!).Items[0]!).Focus(); Dispatcher.UIThread.RunJobs();
            Check(Find<Border>(window, "IdentityPanel").IsVisible && state.Selected == rows[1].Reference, "Collapsed focus hid identity or changed selection.");
            Check(Find<SelectableTextBlock>(window, "FullIdentity").Text!.Contains(remoteB.Value.ToString("D")), "Collapsed inspection shows wrong Host.");
            Capture(window, output, "narrow-identity"); Key(window, PhysicalKey.Escape);
            Click(window, "IdentityDetails"); Click(window, "CloseIdentity");
            Check(!Find<Border>(window, "IdentityPanel").IsVisible && Find<Button>(window, "IdentityDetails").IsFocused, $"Closing identity reopened it or lost focus: visible={Find<Border>(window, "IdentityPanel").IsVisible}, focus={(window.FocusManager?.GetFocusedElement() as Control)?.Name}.");
            Click(window, "ExpandRail"); Check(Find<Border>(window, "ServerRail").Width == 280, "Compact rail did not expand.");
            Key(window, PhysicalKey.Escape); Check(Find<Button>(window, "ExpandRail").IsFocused && Find<Border>(window, "ServerRail").Width == 88, "Expanded rail failed focus return.");
            Click(window, "ManagerSettings"); Click(window, "ThemeLightMinimal"); Check(window.CurrentTheme == ShellTheme.LightMinimal && state.Selected == rows[1].Reference, "Actual theme control changed selection.");
            Key(window, PhysicalKey.Escape); Check(Find<Button>(window, "ManagerSettings").IsFocused, "Settings focus return failed.");
            Click(window, "Activity"); Key(window, PhysicalKey.Tab);
            Check(!Find<Border>(window, "GlobalPanel").IsKeyboardFocusWithin, "Nonmodal Activity trapped Tab."); Key(window, PhysicalKey.Escape);
            window.Width = 640; window.FontSize = 28; Dispatcher.UIThread.RunJobs(); Capture(window, output, "enlarged-640");
            Check(window.FindControl<Grid>("Root")!.Bounds.Width == 640 && window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.FontSize == 28), "Enlarged capture did not lay out actual enlarged text.");
            var tabStrip = Find<ScrollViewer>(window, "ServerTabs"); Check(Find<Button>(window, "TabsRight").IsVisible, "Overflowing tabs have no visible scroll affordance.");
            Click(window, "TabsRight"); Check(tabStrip.Offset.X > 0, "Tab scroll affordance did not navigate overflow."); Click(window, "TabsLeft");
            foreach (var name in new[] { "Activity", "Alerts", "ManagerSettings" })
            {
                var button = Find<Button>(window, name); var position = button.TranslatePoint(default, window)!.Value;
                Check(position.X >= 0 && position.X + button.Bounds.Width <= window.Width && button.Bounds.Height >= 40, "Enlarged chrome action clipped or too small.");
            }
            var expand = Find<Button>(window, "ExpandRail"); var expandPosition = expand.TranslatePoint(default, window)!.Value;
            Check(expandPosition.Y + expand.Bounds.Height <= window.Height && tree.Bounds.Height > 200, "Enlarged rail foot hid navigation or expand control.");
            Check(window.GetVisualDescendants().OfType<Control>().All(control => control.Transitions is null || control.Transitions.Count == 0), "Reduced-motion shell retained transitions: " + string.Join(",", window.GetVisualDescendants().OfType<Control>().Where(c => c.Transitions?.Count > 0).Select(c => c.GetType().Name + ":" + c.Name)));
            state.ReplaceAuthorizedInventory([rows[0]]); Dispatcher.UIThread.RunJobs();
            Check(state.Selected is null && tree.Items.Count == 1 && !Find<Border>(window, "IdentityPanel").IsVisible, "Removed target leaked through UI.");
            var late = new TaskCompletionSource<bool>(); var pendingState = new ShellState(local, (_, _) => late.Task); pendingState.ReplaceAuthorizedInventory(rows);
            window.BindState(pendingState); window.UpdateLayout(); tree = Find<ServerTree>(window, "ServerTree");
            ((TreeViewItem)((TreeViewItem)tree.Items[0]!).Items[0]!).Focus(); Key(window, PhysicalKey.Enter);
            window.BindState(null); late.SetException(new IOException("Old connection failed")); Dispatcher.UIThread.RunJobs();
            Check(window.State is null && tree.Items.Count == 0 && tree.SelectedItem is null && !Find<Border>(window, "IdentityPanel").IsVisible &&
                !window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("could not be verified") == true), "Disconnected view retained rows, selection, identity or stale failure status.");
        }
        finally { window.Close(); }
    }
}
