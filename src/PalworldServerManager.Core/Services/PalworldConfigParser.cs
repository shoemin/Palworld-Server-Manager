using System.Text;

namespace PalworldServerManager.Core.Services;

public sealed class PalworldConfigDocument
{
    public string Prefix { get; init; } = "[/Script/Pal.PalGameWorldSettings]" + Environment.NewLine + "OptionSettings=";
    public string Suffix { get; init; } = Environment.NewLine;
    public List<KeyValuePair<string, string>> Entries { get; } = [];

    public string? Get(string key)
        => Entries.LastOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    public void Set(string key, string value)
    {
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(Entries[i].Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            Entries[i] = new KeyValuePair<string, string>(Entries[i].Key, value);
            return;
        }
        Entries.Add(new KeyValuePair<string, string>(key, value));
    }

    public string Serialize()
    {
        var inner = string.Join(",", Entries.Select(x => $"{x.Key}={x.Value}"));
        return Prefix + "(" + inner + ")" + Suffix;
    }
}

public static class PalworldConfigParser
{
    private const string Marker = "OptionSettings=";

    public static PalworldConfigDocument Parse(string text)
    {
        var markerIndex = text.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            var doc = new PalworldConfigDocument();
            return doc;
        }

        var open = text.IndexOf('(', markerIndex + Marker.Length);
        if (open < 0) throw new FormatException("OptionSettings does not contain an opening parenthesis.");
        var close = FindMatchingParen(text, open);
        if (close < 0) throw new FormatException("OptionSettings does not contain a matching closing parenthesis.");

        var prefix = text[..open];
        var suffix = text[(close + 1)..];
        var inner = text[(open + 1)..close];
        var result = new PalworldConfigDocument { Prefix = prefix, Suffix = suffix };

        foreach (var token in SplitTopLevel(inner, ','))
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            var equals = FindTopLevelEquals(token);
            if (equals <= 0)
            {
                result.Entries.Add(new KeyValuePair<string, string>(token.Trim(), string.Empty));
                continue;
            }
            var key = token[..equals].Trim();
            var value = token[(equals + 1)..].Trim();
            result.Entries.Add(new KeyValuePair<string, string>(key, value));
        }

        return result;
    }

    public static PalworldConfigDocument Load(string path)
        => Parse(File.ReadAllText(path));

    public static string Unquote(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return value;
    }

    public static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static int FindMatchingParen(string text, int open)
    {
        var depth = 0;
        var inQuote = false;
        var escaped = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuote)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inQuote = false;
                continue;
            }
            if (c == '"') { inQuote = true; continue; }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        var start = 0;
        var paren = 0;
        var bracket = 0;
        var brace = 0;
        var inQuote = false;
        var escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuote)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inQuote = false;
                continue;
            }
            if (c == '"') { inQuote = true; continue; }
            switch (c)
            {
                case '(': paren++; break;
                case ')': paren--; break;
                case '[': bracket++; break;
                case ']': bracket--; break;
                case '{': brace++; break;
                case '}': brace--; break;
                default:
                    if (c == separator && paren == 0 && bracket == 0 && brace == 0)
                    {
                        yield return text[start..i];
                        start = i + 1;
                    }
                    break;
            }
        }
        yield return text[start..];
    }

    private static int FindTopLevelEquals(string text)
    {
        var inQuote = false;
        var escaped = false;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuote)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inQuote = false;
                continue;
            }
            if (c == '"') { inQuote = true; continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == '=' && depth == 0) return i;
        }
        return -1;
    }
}
