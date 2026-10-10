using System.Text;
using PalworldServerManager.Core.Settings;
namespace PalworldServerManager.SelfTest;
internal static class PreservedSettingFileTests
{
    private const string Text="; unchanged 世界 e\u0301\r\n[/Script/Pal.PalGameWorldSettings]\r\nOptionSettings=(ExpRate=1.000,Future=(Name=\"世界\"),AdminPassword=\"private\")\r\n; end";
    private static void Check(bool condition){if(!condition)throw new Exception("Preserved settings file assertion failed.");}
    private static byte[] Encode(Encoding encoding,string text)=>encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
    private static Encoding[] Encodings()=>new Encoding[]{new UTF8Encoding(false,true),new UTF8Encoding(true,true),new UnicodeEncoding(false,true,true),new UnicodeEncoding(true,true,true),new UTF32Encoding(false,true,true),new UTF32Encoding(true,true,true)};
    public static Task ExactEncodingAndSelectedEdit()
    {
        foreach(var encoding in Encodings())
        {
            var original=Encode(encoding,Text);var file=PreservedPalworldConfigFile.Parse(original);
            Check(file.Serialize().SequenceEqual(original)&&ReferenceEquals(file,file.WithValues(new Dictionary<string,string>())));
            var changed=file.WithValues(new Dictionary<string,string>{{"ExpRate","2.5"}});
            Check(changed.Serialize().SequenceEqual(Encode(encoding,Text.Replace("ExpRate=1.000","ExpRate=2.5",StringComparison.Ordinal))));
            Check(file.Serialize().SequenceEqual(original)&&changed.Get("AdminPassword")=="\"private\""&&changed.Get("Future")==file.Get("Future"));
        }
        return Task.CompletedTask;
    }
    public static Task DefensiveCopiesAndSecretSafeErrors()
    {
        var input=Encode(new UTF8Encoding(true,true),Text);var expected=input.ToArray();var file=PreservedPalworldConfigFile.Parse(input);
        Array.Fill(input,(byte)0);var returned=file.Serialize();Array.Fill(returned,(byte)0);
        Check(file.Serialize().SequenceEqual(expected)&&!file.ToString()!.Contains("private",StringComparison.Ordinal));
        foreach(var encoding in Encodings())
        {
            var original=PreservedPalworldConfigFile.Parse(Encode(encoding,Text));
            try{original.WithValues(new Dictionary<string,string>{{"Future","\"private\ud800\""}});throw new Exception("Expected encoding refusal.");}
            catch(FormatException e){Check(e.InnerException is null&&!e.Message.Contains("private",StringComparison.Ordinal)&&original.Serialize().SequenceEqual(Encode(encoding,Text)));}
        }
        return Task.CompletedTask;
    }
    public static Task InvalidEncodingAndBoundsRefuse()
    {
        foreach(var bytes in new[]{new byte[]{0xef},new byte[]{0xef,0xbb},new byte[]{0xc0,0xaf},new byte[]{0xff,0xfe,0x41},new byte[]{0xfe,0xff,0xd8,0},new byte[]{0xff,0xfe,0,0,0,0xd8,0,0},new byte[]{0,0,0xfe,0xff,0,0x11,0,0},new byte[PreservedPalworldConfigFile.MaximumBytes+1]})
        {
            try{PreservedPalworldConfigFile.Parse(bytes);throw new Exception("Expected source encoding refusal.");}
            catch(FormatException e){Check(e.InnerException is null&&!e.Message.Contains("private",StringComparison.Ordinal));}
        }
        foreach(var encoding in Encodings())
        {
            var tooLong=Encode(encoding,new string('x',PreservedPalworldConfigDocument.MaximumCharacters+1));
            try{PreservedPalworldConfigFile.Parse(tooLong);throw new Exception("Expected text bound refusal.");}catch(FormatException){}
        }
        return Task.CompletedTask;
    }
}
