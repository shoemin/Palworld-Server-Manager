using System.Collections.ObjectModel;
using System.Text.Json;

namespace PalworldServerManager.Client.Avalonia.Shell;

public enum ShellTheme { Refined, DarkMinimal, LightMinimal }
public readonly record struct ShellLayout(double RailWidth, double DrawerWidth, bool IsCompact, bool DrawerOverlays);

// One accepted token source embedded into the executable; no runtime file/config dependency.
public sealed class ShellTokens
{
    public static ShellTokens Accepted { get; } = new();
    private readonly IReadOnlyDictionary<ShellTheme, IReadOnlyDictionary<string, string>> _palettes;
    public IReadOnlyDictionary<string, double> Dimensions { get; }
    public IReadOnlyDictionary<string, double> Typography { get; }
    public IReadOnlyList<double> Spacing { get; }
    private readonly double _feedback, _panel;
    private ShellTokens()
    {
        using var stream = typeof(ShellTokens).Assembly.GetManifestResourceStream("PalworldServerManager.Shell.Tokens.json")
            ?? throw new InvalidOperationException("Accepted shell tokens are missing.");
        using var document = JsonDocument.Parse(stream); var root = document.RootElement;
        static IReadOnlyDictionary<string, double> Numbers(JsonElement source) => new ReadOnlyDictionary<string, double>(
            source.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetDouble(), StringComparer.Ordinal));
        Dimensions = Numbers(root.GetProperty("dimensions")); Typography = Numbers(root.GetProperty("type"));
        Spacing = Array.AsReadOnly(root.GetProperty("spacing").EnumerateArray().Select(item => item.GetDouble()).ToArray());
        var names = new Dictionary<ShellTheme, string> { [ShellTheme.Refined] = "refined", [ShellTheme.DarkMinimal] = "dark", [ShellTheme.LightMinimal] = "light" };
        _palettes = new ReadOnlyDictionary<ShellTheme, IReadOnlyDictionary<string, string>>(names.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyDictionary<string, string>)new ReadOnlyDictionary<string, string>(root.GetProperty("themes").GetProperty(pair.Value)
                .EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString()!, StringComparer.Ordinal))));
        var motion = root.GetProperty("motion"); _feedback = motion.GetProperty("feedbackMs").GetDouble(); _panel = motion.GetProperty("panelMs").GetDouble();
    }
    public IReadOnlyDictionary<string, string> Palette(ShellTheme theme) => _palettes.TryGetValue(theme, out var value) ? value : throw new ArgumentException("Unknown shell theme.");
    public TimeSpan Feedback(bool reduceMotion) => TimeSpan.FromMilliseconds(reduceMotion ? 0 : _feedback);
    public TimeSpan Panel(bool reduceMotion) => TimeSpan.FromMilliseconds(reduceMotion ? 0 : _panel);
    public ShellLayout Layout(double width)
    {
        if (!double.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var compact = width < 1200;
        return new(Dimensions[compact ? "collapsedRail" : "rail"], Dimensions["drawer"], compact, compact);
    }
}
