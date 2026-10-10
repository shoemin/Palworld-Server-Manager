using System.Text;
using PalworldServerManager.Core.Services;

namespace PalworldServerManager.Core.Settings;

// Host-owned in-memory document; never a client DTO or a loggable record. Compose
// the existing parser for semantic interpretation while retaining original spans.
public sealed class PreservedPalworldConfigDocument
{
    public const int MaximumCharacters=1024*1024;
    private readonly string original;
    private readonly PalworldConfigDocument semantic;
    private readonly IReadOnlyList<Entry> entries;
    private readonly int close;
    private sealed record Entry(string Key,int ValueStart,int ValueEnd);
    private PreservedPalworldConfigDocument(string text,PalworldConfigDocument semantic,List<Entry> entries,int close)
    {original=text;this.semantic=semantic;this.entries=entries.AsReadOnly();this.close=close;}
    public string Serialize()=>original;
    public string? Get(string key)=>semantic.Get(key);
    public IReadOnlyList<string> Keys=>Array.AsReadOnly(entries.Select(e=>e.Key).ToArray());

    public static PreservedPalworldConfigDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if(text.Length>MaximumCharacters||text.Contains('\0'))throw Invalid();
        const string marker="OptionSettings=";
        var starts=new List<int>();var offset=0;var section=false;
        foreach(var line in text.Split('\n'))
        {
            var content=line.Trim();
            if(content.StartsWith('['))section=content.Equals("[/Script/Pal.PalGameWorldSettings]",StringComparison.OrdinalIgnoreCase);
            if(content.StartsWith(marker,StringComparison.OrdinalIgnoreCase))
            {
                if(!section)throw Invalid();
                starts.Add(offset+line.IndexOf(marker,StringComparison.OrdinalIgnoreCase)+marker.Length);
            }
            offset+=line.Length+1;
        }
        if(starts.Count!=1)throw Invalid();
        var open=starts[0];while(open<text.Length&&char.IsWhiteSpace(text[open]))open++;
        if(open==text.Length||text[open]!='(')throw Invalid();
        var spans=Scan(text,open,out var close);
        // The legacy parser selects its first marker; reject ambiguity instead of
        // silently assigning new semantics to comments/quoted marker lookalikes.
        if(text.IndexOf(marker,StringComparison.OrdinalIgnoreCase)!=starts[0]-marker.Length)throw Invalid();
        var parsed=PalworldConfigParser.Parse(text);var entries=new List<Entry>();
        foreach(var (start,end) in spans)
        {
            if(string.IsNullOrWhiteSpace(text[start..end]))
            {if(spans.Count!=1)throw Invalid();continue;}
            var equal=text.IndexOf('=',start,end-start);
            if(equal<=start)throw Invalid();
            var key=text[start..equal].Trim();if(!Key(key))throw Invalid();
            var valueStart=equal+1;while(valueStart<end&&char.IsWhiteSpace(text[valueStart]))valueStart++;
            var valueEnd=end;while(valueEnd>valueStart&&char.IsWhiteSpace(text[valueEnd-1]))valueEnd--;
            entries.Add(new(key,valueStart,valueEnd));
        }
        if(entries.Count!=parsed.Entries.Count)throw Invalid();
        for(var i=0;i<entries.Count;i++)
            if(entries[i].Key!=parsed.Entries[i].Key||text[entries[i].ValueStart..entries[i].ValueEnd]!=parsed.Entries[i].Value)throw Invalid();
        // After the closing tuple only whitespace or a comment is accepted on that
        // physical line. The rest of the text remains character-for-character untouched.
        var lineEnd=text.IndexOf('\n',close+1);if(lineEnd<0)lineEnd=text.Length;
        var tail=text[(close+1)..lineEnd].TrimStart();
        if(tail.Length!=0&&!tail.StartsWith(';')&&!tail.StartsWith('#'))throw Invalid();
        return new(text,parsed,entries,close);
    }

    // Values are already serialized, structurally complete INI expressions from
    // trusted Host validation. This is no authorization or secret-edit boundary.
    public PreservedPalworldConfigDocument WithValues(IReadOnlyDictionary<string,string> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);if(changes.Count==0)return this;
        if(changes.Count>MaximumCharacters/3)throw Invalid();
        // Bound aggregate input before any concatenation or per-value parsing.
        // Index once so many explicit edits cannot produce quadratic scans.
        var inputSize=0L;
        foreach(var pair in changes)
        {
            if(pair.Key is null||pair.Value is null)throw Invalid();
            inputSize+=(long)pair.Key.Length+pair.Value.Length;
            if(inputSize>MaximumCharacters*2L)throw Invalid();
        }
        var byKey=entries.ToLookup(e=>e.Key,StringComparer.OrdinalIgnoreCase);
        var edits=new List<(int Start,int End,string Value)>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var added=new List<string>();
        foreach(var pair in changes)
        {
            if(!Key(pair.Key)||!seen.Add(pair.Key)||pair.Value is null||pair.Value.Length>MaximumCharacters||pair.Value.Trim()!=pair.Value)throw Invalid();
            RequireValue(pair.Value);
            var matches=byKey[pair.Key].ToArray();
            if(matches.Length>1)throw new InvalidOperationException("Duplicate setting requires explicit document repair.");
            if(matches.Length==1)edits.Add((matches[0].ValueStart,matches[0].ValueEnd,pair.Value));
            else added.Add(pair.Key+"="+pair.Value);
        }
        if(added.Count>0)edits.Add((close,close,(entries.Count>0?",":"")+string.Join(',',added)));
        var total=(long)original.Length+edits.Sum(e=>(long)e.Value.Length-(e.End-e.Start));
        if(total>MaximumCharacters)throw Invalid();
        var result=new StringBuilder(original);
        foreach(var edit in edits.OrderByDescending(e=>e.Start))result.Remove(edit.Start,edit.End-edit.Start).Insert(edit.Start,edit.Value);
        if(result.Length>MaximumCharacters)throw Invalid();
        return Parse(result.ToString());
    }

    private static void RequireValue(string value)
    {
        if(value.Length==0||value.Any(char.IsControl))throw Invalid();
        // A complete one-entry tuple must survive both structural scanning and the
        // original semantic parser. No closing delimiter can inject another key.
        var test="[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(Value="+value+")";
        var parsed=Parse(test);
        if(parsed.entries.Count!=1||parsed.Get("Value")!=value)throw Invalid();
        var quote=false;var escape=false;
        foreach(var c in value)
        {
            if(quote){if(escape)escape=false;else if(c=='\\')escape=true;else if(c=='"')quote=false;}
            else if(c=='"')quote=true;
            else if(c is ';' or '#')throw Invalid();
        }
    }
    private static bool Key(string? key)=>!string.IsNullOrEmpty(key)&&(char.IsLetter(key[0])||key[0]=='_')&&key.All(c=>char.IsLetterOrDigit(c)||c is '_' or '.' or '-');
    private static FormatException Invalid()=>new("Settings document has unsupported or ambiguous syntax.");
    private static List<(int Start,int End)> Scan(string text,int open,out int close)
    {
        var spans=new List<(int,int)>();var stack=new Stack<char>();stack.Push('(');var quote=false;var escape=false;var start=open+1;
        for(var i=start;i<text.Length;i++)
        {
            var c=text[i];
            if(quote){if(escape)escape=false;else if(c=='\\')escape=true;else if(c=='"')quote=false;continue;}
            if(c=='"'){quote=true;continue;}
            if(c is '(' or '[' or '{'){if(stack.Count>=64)throw Invalid();stack.Push(c);continue;}
            if(c is ')' or ']' or '}')
            {
                if(stack.Count==0||stack.Pop()!=(c==')'?'(':c==']'?'[':'{'))throw Invalid();
                if(stack.Count==0){spans.Add((start,i));close=i;return spans;}
            }
            else if(c==','&&stack.Count==1){spans.Add((start,i));start=i+1;}
        }
        throw Invalid();
    }
}
