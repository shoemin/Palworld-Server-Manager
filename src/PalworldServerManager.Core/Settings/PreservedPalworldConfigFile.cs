using System.Text;

namespace PalworldServerManager.Core.Settings;

// Pure Host-side byte snapshot, not a path API, DTO or loggable record.
public sealed class PreservedPalworldConfigFile
{
    public const int MaximumBytes=PreservedPalworldConfigDocument.MaximumCharacters*4+4;
    private readonly byte[] bytes;
    private readonly byte[] preamble;
    private readonly Encoding encoding;
    private readonly PreservedPalworldConfigDocument document;
    private PreservedPalworldConfigFile(byte[] bytes,byte[] preamble,Encoding encoding,PreservedPalworldConfigDocument document)
    {this.bytes=bytes;this.preamble=preamble;this.encoding=encoding;this.document=document;}
    public byte[] Serialize()=>bytes.ToArray();
    public string? Get(string key)=>document.Get(key);
    public IReadOnlyList<string> Keys=>document.Keys;

    public static PreservedPalworldConfigFile Parse(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length>MaximumBytes)throw Invalid();
        var snapshot=bytes.ToArray();bytes=snapshot;
        Encoding encoding;var length=0;
        if(bytes.StartsWith(new byte[]{0xff,0xfe,0x00,0x00})){encoding=new UTF32Encoding(false,false,true);length=4;}
        else if(bytes.StartsWith(new byte[]{0x00,0x00,0xfe,0xff})){encoding=new UTF32Encoding(true,false,true);length=4;}
        else if(bytes.StartsWith(new byte[]{0xef,0xbb,0xbf})){encoding=new UTF8Encoding(false,true);length=3;}
        else if(bytes.StartsWith(new byte[]{0xff,0xfe})){encoding=new UnicodeEncoding(false,false,true);length=2;}
        else if(bytes.StartsWith(new byte[]{0xfe,0xff})){encoding=new UnicodeEncoding(true,false,true);length=2;}
        else encoding=new UTF8Encoding(false,true);
        string text;
        try
        {
            if(encoding.GetCharCount(bytes[length..])>PreservedPalworldConfigDocument.MaximumCharacters)throw Invalid();
            text=encoding.GetString(bytes[length..]);
            if(!encoding.GetBytes(text).AsSpan().SequenceEqual(bytes[length..]))throw Invalid();
        }
        catch(DecoderFallbackException){throw Invalid();}
        catch(EncoderFallbackException){throw Invalid();}
        var document=PreservedPalworldConfigDocument.Parse(text);
        return new(snapshot,bytes[..length].ToArray(),encoding,document);
    }

    public PreservedPalworldConfigFile WithValues(IReadOnlyDictionary<string,string> changes)
    {
        var next=document.WithValues(changes);if(ReferenceEquals(next,document))return this;
        byte[] body;
        try{body=encoding.GetBytes(next.Serialize());}
        catch(EncoderFallbackException){throw Invalid();}
        if(body.Length>MaximumBytes-preamble.Length)throw Invalid();
        var output=new byte[preamble.Length+body.Length];preamble.CopyTo(output,0);body.CopyTo(output,preamble.Length);
        return Parse(output);
    }
    private static FormatException Invalid()=>new("Settings file has an unsupported or invalid encoding.");
}
