using PalworldServerManager.Client.Avalonia.Shell;
using PalworldServerManager.Contracts;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static class ShellStateTests
{
    private static readonly HostId Local = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly HostId RemoteA = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-0000-ffffffffffff"));
    private static readonly HostId RemoteB = new(Guid.Parse("aaaaaaaa-bbbb-bbbb-0000-ffffffffffff"));
    private static readonly Guid Profile = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static ShellServer Row(HostId host, string name = "Main Server") => new(new(host, Profile), name, "Family PC");
    private static void Reject(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid shell input was accepted."); }
    public static async Task ExactIdentityAndFocus()
    {
        ServerRef? validated = null; var state = new ShellState(Local, (target, _) => { validated = target; return Task.FromResult(true); });
        state.ReplaceAuthorizedInventory([Row(Local), Row(RemoteA), Row(RemoteB)]);
        var first = state.Rows[1]; var second = state.Rows[2];
        Check(first.HostAlias != second.HostAlias && first.HostDiscriminator != second.HostDiscriminator, "Colliding labels/UUID suffixes remain ambiguous.");
        Check(!first.IsLocal && state.Rows[0].IsLocal && state.Rows[0].HostAlias == "L", "Local identity inferred from display name.");
        foreach (var row in state.Rows)
            Check(row.AccessibleName.Contains(row.Reference.AuthoritativeHostId.Value.ToString("D")) && row.AccessibleName.Contains(Profile.ToString("D")), "Accessible identity omitted exact Host/server IDs.");
        state.Focus(second.Reference); Check(state.Selected is null && state.FocusedRow == second && validated is null, "Focus inspection created selection/Host traffic.");
        Check(await state.SelectAsync(first.Reference) && validated == first.Reference && state.Selected == first.Reference && state.Focused == second.Reference,
            "Exact selection used a label, profile-only ID or focus target.");
        var forged = new ServerRef(new HostId(Guid.NewGuid()), Profile);
        Check(!await state.SelectAsync(forged) && validated == first.Reference, "Hidden target reached Host selection seam.");
        Reject(() => state.Focus(forged)); Reject(() => state.Show(forged.AuthoritativeHostId));
    }
    public static async Task InventoryAndAliases()
    {
        var state = new ShellState(Local, (_, _) => Task.FromResult(true)); var original = Row(RemoteA); var other = Row(RemoteB);
        var input = new[] { original, other }; state.ReplaceAuthorizedInventory(input); var alias = state.Rows[0].HostAlias; var serverAlias = state.Rows[0].ServerAlias;
        input[0] = Row(Local); Check(state.Rows[0].Reference == original.Reference, "Caller mutated accepted inventory through its input array.");
        await state.SelectAsync(original.Reference); state.Focus(original.Reference); state.Show(RemoteA);
        Check(state.VisibleRows.Count() == 1 && state.Selected == original.Reference, "Host scope lost exact selection.");
        state.ReplaceAuthorizedInventory([other]);
        Check(state.Scope is null && state.Selected is null && state.Focused is null, "Removed authority survived in selection, focus or hidden group.");
        state.ReplaceAuthorizedInventory([Row(Local), other, original with { Name = "Renamed" }]);
        var restored = state.Rows.Single(row => row.Reference == original.Reference);
        Check(restored.HostAlias == alias && restored.ServerAlias == serverAlias && restored.Name == "Renamed", "Alias changed across reorder/removal/rename.");
        Check(state.Rows.Select(row => row.HostAlias).Distinct().Count() == 3, "A retired alias was reused for a different Host.");
        var before = state.Rows;
        Reject(() => state.ReplaceAuthorizedInventory([original, original]));
        Reject(() => state.ReplaceAuthorizedInventory([original, new(new(RemoteA, Guid.NewGuid()), "Other", "Conflicting PC")]));
        Check(ReferenceEquals(before, state.Rows), "Rejected inventory partially mutated state.");
        state.ReplaceAuthorizedInventory([Row(Local, "Main\u202e\n\u2028\u2029Server")]);
        Check(state.Rows[0].Name == "Main\uFFFD\uFFFD\uFFFD\uFFFDServer", "Display controls or Unicode separators could forge identity layout.");
        state.Show(Local); state.Disconnect(); Check(state.Rows.Count == 0 && state.Selected is null && !state.IsSelectionPending, "Disconnect retained selectable cached authority.");
    }
    public static async Task StaleSelection()
    {
        var replies = new Queue<TaskCompletionSource<bool>>();
        var state = new ShellState(Local, (_, _) => { var next = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); replies.Enqueue(next); return next.Task; });
        var a = Row(RemoteA); var b = Row(RemoteB); state.ReplaceAuthorizedInventory([a, b]);
        var old = state.SelectAsync(a.Reference); var oldReply = replies.Dequeue();
        var latest = state.SelectAsync(b.Reference); var latestReply = replies.Dequeue();
        latestReply.SetResult(true); Check(await latest && state.Selected == b.Reference, "Latest selection did not commit.");
        oldReply.SetResult(true); Check(!await old && state.Selected == b.Reference, "Out-of-order reply redirected selection.");
        var removed = state.SelectAsync(a.Reference); var removedReply = replies.Dequeue(); state.ReplaceAuthorizedInventory([b]);
        removedReply.SetResult(true); Check(!await removed && state.Selected == b.Reference && !state.IsSelectionPending, "Removed target reply survived inventory replacement.");
        var denied = state.SelectAsync(b.Reference); replies.Dequeue().SetResult(false);
        Check(!await denied && state.Selected is null, "Failed revalidation retained selected target.");
        var restored = state.SelectAsync(b.Reference); replies.Dequeue().SetResult(true); await restored;
        var failed = state.SelectAsync(b.Reference); replies.Dequeue().SetException(new IOException("Disconnected"));
        try { await failed; throw new Exception("Failed selection succeeded."); } catch (IOException) { }
        Check(state.Selected is null && !state.IsSelectionPending, "Failed transport retained selected target.");
        restored = state.SelectAsync(b.Reference); replies.Dequeue().SetResult(true); await restored;
        using var stop = new CancellationTokenSource(); var canceled = state.SelectAsync(b.Reference, stop.Token); var canceledReply = replies.Dequeue();
        stop.Cancel(); canceledReply.SetResult(true);
        try { await canceled; throw new Exception("Canceled selection succeeded."); } catch (OperationCanceledException) { }
        Check(state.Selected is null && !state.IsSelectionPending, "Cancellation retained pending/selected state.");
    }
    public static Task TokensAndResponsiveRules()
    {
        var tokens = ShellTokens.Accepted;
        var wide = tokens.Layout(2100); var normal = tokens.Layout(1600); var narrow = tokens.Layout(800);
        Check(wide == normal && wide.RailWidth == 280 && wide.DrawerWidth == 304 && !wide.DrawerOverlays && narrow.RailWidth == 88 && narrow.DrawerOverlays,
            "Accepted wide/narrow layout contract drifted.");
        Check(tokens.Layout(1199).IsCompact && !tokens.Layout(1200).IsCompact && tokens.Layout(480).RailWidth == 88, "Compact rail disappears or breakpoint drifted.");
        Check(tokens.Feedback(true) == TimeSpan.Zero && tokens.Panel(true) == TimeSpan.Zero && tokens.Feedback(false).TotalMilliseconds == 100 && tokens.Panel(false).TotalMilliseconds == 160,
            "Reduce motion did not remove motion.");
        Reject(() => tokens.Layout(double.NaN)); Reject(() => tokens.Layout(0)); Reject(() => tokens.Palette((ShellTheme)999));
        var keys = tokens.Palette(ShellTheme.Refined).Keys.Order().ToArray();
        foreach (var theme in Enum.GetValues<ShellTheme>())
        {
            var palette = tokens.Palette(theme); Check(palette.Keys.Order().SequenceEqual(keys), "Theme changes component/token structure.");
            static double Luminance(string color)
            {
                var channels = Convert.FromHexString(color[1..]).Select(value => value / 255.0).Select(value => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4)).ToArray();
                return .2126 * channels[0] + .7152 * channels[1] + .0722 * channels[2];
            }
            static double Contrast(double a, double b) => (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
            foreach (var background in new[] { "canvas", "surface", "raised", "selected" })
            {
                foreach (var text in new[] { "text", "muted", "success", "warning", "danger" })
                    Check(Contrast(Luminance(palette[text]), Luminance(palette[background])) >= 4.5, "Accepted semantic text contrast regressed.");
                Check(Contrast(Luminance(palette["accent"]), Luminance(palette[background])) >= 3, "Focus contrast regressed.");
            }
        }
        return Task.CompletedTask;
    }
}
