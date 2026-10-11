using System.Text;
using System.Text.Json;
using PalworldServerManager.Core.Services;
using PalworldServerManager.Core.Settings;

namespace PalworldServerManager.SelfTest;
internal static class SettingPatchTests
{
    private const string Text="; keep\r\n[/Script/Pal.PalGameWorldSettings]\r\nOptionSettings=(ExpRate=1.000,bEnableFastTravel=False,ServerName=\"old\",AdminPassword=\"private-original\",_Future=(X=[1,2]),DeathPenalty=FutureChoice)\r\n; tail";
    private static PreservedPalworldConfigFile Source(string text=Text)=>PreservedPalworldConfigFile.Parse(Encoding.UTF8.GetBytes(text));
    private static void Check(bool value){if(!value)throw new Exception("Semantic patch assertion failed.");}
    public static Task SelectedEditsPreserveEveryOtherByte()
    {
        var source=Source();var result=PalworldSettingPatch.Prepare(source,new[]{PalworldSettingEdit.Set("ExpRate","2.5"),PalworldSettingEdit.Set("bEnableFastTravel","TRUE"),PalworldSettingEdit.Set("ServerName","")});
        Check(result.IsValid&&result.Errors.Count==0&&result.Output!.Serialize().SequenceEqual(Encoding.UTF8.GetBytes(Text.Replace("ExpRate=1.000","ExpRate=2.5").Replace("bEnableFastTravel=False","bEnableFastTravel=True").Replace("ServerName=\"old\"","ServerName=\"\""))));
        Check(source.Serialize().SequenceEqual(Encoding.UTF8.GetBytes(Text)));
        Check(ReferenceEquals(source,PalworldSettingPatch.Prepare(source,new[]{PalworldSettingEdit.Unchanged("AdminPassword"),PalworldSettingEdit.Unchanged("_Future"),PalworldSettingEdit.Unchanged("DeathPenalty")}).Output));
        Check(ReferenceEquals(source,PalworldSettingPatch.Prepare(source,Array.Empty<PalworldSettingEdit>()).Output));
        return Task.CompletedTask;
    }
    public static Task SecretActionsNeverImplicitlyClearOrEcho()
    {
        var source=Source();
        foreach(var edit in new[]{PalworldSettingEdit.Set("AdminPassword","private-new"),PalworldSettingEdit.ReplaceSecret("AdminPassword","")})
        {
            var result=PalworldSettingPatch.Prepare(source,new[]{edit});Check(!result.IsValid&&result.Output is null&&result.Errors.Count==1);
            Check(!edit.ToString()!.Contains("private-new",StringComparison.Ordinal)&&!JsonSerializer.Serialize(edit).Contains("private-new",StringComparison.Ordinal));
            Check(!JsonSerializer.Serialize(result.Errors).Contains("private",StringComparison.Ordinal));
        }
        Check(PalworldSettingPatch.Prepare(source,new[]{PalworldSettingEdit.Set("AdminPassword","x")}).Errors.Single().Validation==SettingValidationCode.ExplicitSecretActionRequired);
        Check(PalworldSettingPatch.Prepare(source,new[]{PalworldSettingEdit.ReplaceSecret("AdminPassword","")}).Errors.Single().Code==SettingPatchCode.EmptySecretReplacement);
        Check(PalworldSettingPatch.Prepare(source,new[]{PalworldSettingEdit.ClearSecret("AdminPassword")}).Output!.Get("AdminPassword")=="\"\"");
        var replacement=PalworldSettingEdit.ReplaceSecret("AdminPassword","  private-new  ");
        Check(!JsonSerializer.Serialize(replacement).Contains("private-new",StringComparison.Ordinal));
        var changed=PalworldSettingPatch.Prepare(source,new[]{replacement});
        Check(PalworldConfigParser.Unquote(changed.Output!.Get("AdminPassword"))=="  private-new  "&&source.Get("AdminPassword")=="\"private-original\"");
        return Task.CompletedTask;
    }
    public static Task WholePatchRefusesInvalidOrUnsupportedEdits()
    {
        var source=Source();
        var edits=new[]{PalworldSettingEdit.Set("ExpRate","bad"),PalworldSettingEdit.Set("_Future","private"),PalworldSettingEdit.Set("CrossplayPlatforms","(Steam)"),PalworldSettingEdit.ClearSecret("ServerName"),PalworldSettingEdit.ReplaceSecret("ServerName","private"),PalworldSettingEdit.Set("DeathPenalty","FutureChoice"),PalworldSettingEdit.Set("ServerName","private\u2028"),PalworldSettingEdit.ReplaceSecret("AdminPassword","private\n"),PalworldSettingEdit.ReplaceSecret("AdminPassword","private\ud800")};
        foreach(var edit in edits)
        {
            var result=PalworldSettingPatch.Prepare(source,new[]{PalworldSettingEdit.Set("bEnableFastTravel","True"),edit});
            Check(!result.IsValid&&result.Output is null&&result.Errors.Single().EditIndex==1&&source.Serialize().SequenceEqual(Encoding.UTF8.GetBytes(Text)));
            Check(!JsonSerializer.Serialize(result.Errors).Contains("private",StringComparison.Ordinal));
        }
        return Task.CompletedTask;
    }
    public static Task DuplicateAndMalformedRequestsRefuse()
    {
        var source=Source();
        Check(PalworldSettingPatch.Prepare(source,new[]{PalworldSettingEdit.Set("ExpRate","2"),PalworldSettingEdit.Set("exprate","3")}).Errors.Single().Code==SettingPatchCode.DuplicateEdit);
        var duplicate=Source(Text.Replace("ExpRate=1.000","ExpRate=1,exprate=2"));
        Check(PalworldSettingPatch.Prepare(duplicate,new[]{PalworldSettingEdit.Set("ExpRate","3")}).Errors.Single().Code==SettingPatchCode.DuplicateSource);
        Check(PalworldSettingPatch.Prepare(duplicate,new[]{PalworldSettingEdit.Set("ServerName","new")}).IsValid);
        Check(PalworldSettingPatch.Prepare(source,new[]{PalworldSettingEdit.Set("bad,private","x")}).Errors.Single().Code==SettingPatchCode.InvalidRequest);
        Check(PalworldSettingPatch.Prepare(source,new PalworldSettingEdit[]{null!}).Errors.Single().Code==SettingPatchCode.InvalidRequest);
        Check(PalworldSettingPatch.Prepare(source,Enumerable.Range(0,PalworldSettingPatch.MaximumEdits+1).Select(i=>PalworldSettingEdit.Unchanged("Key"+i)).ToArray()).Errors.Single().Code==SettingPatchCode.InvalidRequest);
        Check(PalworldSettingPatch.Prepare(source,new[]{PalworldSettingEdit.ReplaceSecret("AdminPassword",new string('x',PreservedPalworldConfigDocument.MaximumCharacters))}).Errors.Single().Code==SettingPatchCode.InvalidRequest);
        return Task.CompletedTask;
    }
    public static Task AllSupportedEncodingsRemainExact()
    {
        var formats=new Encoding[]{new UTF8Encoding(false,true),new UTF8Encoding(true,true),new UnicodeEncoding(false,true,true),new UnicodeEncoding(true,true,true),new UTF32Encoding(false,true,true),new UTF32Encoding(true,true,true)};
        foreach(var format in formats)
        {
            var original=format.GetPreamble().Concat(format.GetBytes(Text)).ToArray();var source=PreservedPalworldConfigFile.Parse(original);
            var result=PalworldSettingPatch.Prepare(source,new[]{PalworldSettingEdit.Set("ExpRate","4.5"),PalworldSettingEdit.ReplaceSecret("AdminPassword","世界-new")});
            var expected=Text.Replace("ExpRate=1.000","ExpRate=4.5").Replace("private-original","世界-new");
            Check(result.IsValid&&result.Output!.Serialize().SequenceEqual(format.GetPreamble().Concat(format.GetBytes(expected)))&&source.Serialize().SequenceEqual(original));
        }
        return Task.CompletedTask;
    }
    public static Task SeededQuotedEditsCannotInjectSettings()
    {
        var random=new Random(4706);var alphabet="abc\\\";,()[]{}= -_世界";var source=Source();
        for(var i=0;i<1000;i++)
        {
            var value=new string(Enumerable.Range(0,random.Next(1,100)).Select(_=>alphabet[random.Next(alphabet.Length)]).ToArray());
            var result=PalworldSettingPatch.Prepare(source,new[]{PalworldSettingEdit.Set("ServerName",value),PalworldSettingEdit.ReplaceSecret("AdminPassword",value)});
            Check(result.IsValid&&PalworldConfigParser.Unquote(result.Output!.Get("ServerName"))==value&&PalworldConfigParser.Unquote(result.Output.Get("AdminPassword"))==value);
            Check(result.Output!.Keys.SequenceEqual(source.Keys)&&result.Output.Get("_Future")==source.Get("_Future")&&result.Output.Get("DeathPenalty")=="FutureChoice");
        }
        return Task.CompletedTask;
    }
}
