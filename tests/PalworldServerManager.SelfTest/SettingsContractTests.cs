using System.Globalization;
using Google.Protobuf;
using PalworldServerManager.Contracts;
using PalworldServerManager.Host;
using Wire = PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.SelfTest;

internal static class SettingsContractTests
{
    private static readonly ServerRef Server = new(new HostId(Guid.NewGuid()), Guid.NewGuid());
    private static Dictionary<string,string> Values(params (string Key,string Value)[] values) => values.ToDictionary(v=>v.Key,v=>v.Value);
    private static Wire.SettingsSnapshot Map(Dictionary<string,string> values, Dictionary<string,string>? defaults = null) =>
        SettingsContractMapping.Snapshot(Server, Guid.NewGuid(), values, defaults ?? new());
    private static void Check(bool value) { if (!value) throw new Exception("Settings contract assertion failed."); }
    public static Task KindsAndAbsentMetadata()
    {
        var cases = new[] { ("Future", "opaque", Wire.SettingsEditorKind.Raw), ("bEnableFastTravel", "True", Wire.SettingsEditorKind.Boolean),
            ("ServerPlayerMaxNum", "32", Wire.SettingsEditorKind.Integer), ("ExpRate", "1.5", Wire.SettingsEditorKind.Multiplier),
            ("PalEggDefaultHatchingTime", "2", Wire.SettingsEditorKind.Duration), ("DeathPenalty", "None", Wire.SettingsEditorKind.Enum),
            ("ServerName", "世界 e\u0301", Wire.SettingsEditorKind.Text), ("AdminPassword", "private", Wire.SettingsEditorKind.Password),
            ("CrossplayPlatforms", "(Steam,Xbox)", Wire.SettingsEditorKind.Compound) };
        var result = Map(Values(cases.Select(c=>(c.Item1,c.Item2)).ToArray()));
        Check(SettingsWireValidation.IsValid(result) && ServerRef.FromWire(result.Server) == Server);
        foreach (var (key,value,kind) in cases)
        {
            var entry = result.Entries.Single(e=>e.Definition.Key==key); var d = entry.Definition;
            Check(d.EditorKind == kind && !d.HasMinimum && !d.HasMaximum && !d.HasStep && !d.HasCatalogDefault && !d.HasRestartRequired && entry.Effective is null);
            if (d.Sensitivity == Wire.SettingsSensitivity.Nonsecret) Check(entry.Configured.Value == value);
            else Check(entry.Configured is null);
        }
        Check(result.Entries.Single(e=>e.Definition.Key=="PalEggDefaultHatchingTime").Definition.Unit=="hours");
        Check(Wire.SettingsSnapshot.Parser.ParseFrom(result.ToByteArray()).Equals(result));
        Check(!typeof(SettingsWireValidation).Assembly.GetReferencedAssemblies().Any(a=>a.Name!.Contains("Core",StringComparison.Ordinal)));
        return Task.CompletedTask;
    }
    public static Task RedactionAllSourcesAndCopies()
    {
        var inputs = Values(("AdminPassword","admin-private"),("ServerPassword","server-private"),("FutureToken","future-private"),("RandomizerSeed","unclassified-private"),("ServerName","visible"));
        var defaults = inputs.ToDictionary(p=>p.Key,p=>p.Value+"-default");
        var result=Map(inputs,defaults); var bytes=result.ToByteArray();
        foreach(var entry in result.Entries.Where(e=>e.Definition.Sensitivity != Wire.SettingsSensitivity.Nonsecret))
            Check(entry.Configured is null && entry.InstallationDefault is null && entry.Effective is null && !entry.Definition.HasCatalogDefault);
        Check(!result.ToString().Contains("private",StringComparison.Ordinal));
        inputs["ServerName"]="changed"; defaults.Clear(); Check(result.ToByteArray().SequenceEqual(bytes));
        var another=Map(Values(("DeathPenalty","None"))); another.Entries[0].Definition.Choices.Clear();
        Check(Map(Values(("DeathPenalty","None"))).Entries[0].Definition.Choices.Count==4);
        return Task.CompletedTask;
    }
    public static Task DistinctObservationsAndHistoricalInvalidValues()
    {
        var culture=CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
            var result=Map(Values(("ExpRate","1,5"),("DeathPenalty","FutureChoice"),("ServerName","old\u2028name")),Values(("ExpRate","1.000"),("ServerDescription","")));
            var exp=result.Entries.Single(e=>e.Definition.Key=="ExpRate");
            Check(exp.Configured.Value=="1,5" && exp.Configured.Validation==Wire.SettingsValidationCode.FiniteNumberRequired && exp.InstallationDefault.Value=="1.000" && exp.InstallationDefault.Validation==Wire.SettingsValidationCode.Valid);
            Check(result.Entries.Single(e=>e.Definition.Key=="DeathPenalty").Configured.Validation==Wire.SettingsValidationCode.KnownChoiceRequired);
            var name=result.Entries.Single(e=>e.Definition.Key=="ServerName"); Check(name.Configured.Value=="old\u2028name" && name.Configured.Validation==Wire.SettingsValidationCode.SingleLineRequired);
            var description=result.Entries.Single(e=>e.Definition.Key=="ServerDescription"); Check(description.Configured is null && description.InstallationDefault.Value=="");
            Check(SettingsWireValidation.IsValid(result) && result.Entries.All(e=>e.Effective is null));
        }
        finally { CultureInfo.CurrentCulture=culture; }
        return Task.CompletedTask;
    }
    public static Task MalformedAndDisclosureShapesRefuse()
    {
        var valid=Map(Values(("ExpRate","1")));
        foreach(var n in new[]{-1,0,999})
        {
            var copy=valid.Clone();copy.Entries[0].Definition.EditorKind=(Wire.SettingsEditorKind)n;Check(!SettingsWireValidation.IsValid(copy));
            copy=valid.Clone();copy.Entries[0].Definition.Sensitivity=(Wire.SettingsSensitivity)n;Check(!SettingsWireValidation.IsValid(copy));
            copy=valid.Clone();copy.Entries[0].Configured.Validation=(Wire.SettingsValidationCode)n;Check(!SettingsWireValidation.IsValid(copy));
        }
        foreach(var key in new[]{"AdminPassword","FutureSecret"})
        foreach(var slot in new[]{0,1,2,3})
        {
            var copy=Map(Values((key,"private"))); var entry=copy.Entries[0];var value=new Wire.SettingsValueObservation{Value="private",Validation=Wire.SettingsValidationCode.Valid};
            if(slot==0)entry.Configured=value;if(slot==1)entry.InstallationDefault=value;if(slot==2)entry.Effective=value;if(slot==3)entry.Definition.CatalogDefault="private";
            Check(!SettingsWireValidation.IsValid(copy));
        }
        var duplicate=valid.Clone();duplicate.Entries.Add(valid.Entries[0].Clone());duplicate.Entries[1].Definition.Key="exprate";Check(!SettingsWireValidation.IsValid(duplicate));
        var bad=valid.Clone();bad.Revision="password-content-hash";Check(!SettingsWireValidation.IsValid(bad));
        bad=valid.Clone();bad.Server.AuthoritativeHostId=Guid.Empty.ToString("D");Check(!SettingsWireValidation.IsValid(bad));
        bad=valid.Clone();bad.Entries[0].Configured.Value="\ud800";Check(!SettingsWireValidation.IsValid(bad));
        bad=valid.Clone();bad.Entries[0].Definition.Choices.Add("unexpected");Check(!SettingsWireValidation.IsValid(bad));
        bad=valid.Clone();bad.Entries[0].Definition.Minimum="1,5";Check(!SettingsWireValidation.IsValid(bad));
        return Task.CompletedTask;
    }
    public static Task BoundsAndConstantErrors()
    {
        foreach(var input in new[]{Values(("ExpRate","1"),("exprate","2")),Values(("bad,key","private")),Values(("ServerName",new string('x',SettingsWireValidation.MaximumCharacters))),Values(("ServerName","private\ud800"))})
        {
            try{Map(input);throw new Exception("Expected mapping refusal.");}
            catch(InvalidDataException e){Check(e.InnerException is null && e.Message=="Settings observation cannot be represented safely.");}
        }
        var many=Enumerable.Range(0,SettingsWireValidation.MaximumEntries+1).ToDictionary(i=>"Future"+i,i=>"private");
        try{Map(many);throw new Exception("Expected entry bound refusal.");}catch(InvalidDataException){}
        var valid=Map(Values(("ServerName",""))); valid.Entries[0].Configured.Value=new string('x',SettingsWireValidation.MaximumCharacters);
        Check(!SettingsWireValidation.IsValid(valid));
        var a=Map(Values(("ServerName","x")));var b=Map(Values(("ServerName","x")));b.Server.AuthoritativeHostId=Guid.NewGuid().ToString("D");
        Check(ServerRef.FromWire(a.Server)!=ServerRef.FromWire(b.Server));
        try{SettingsContractMapping.Snapshot(Server,Guid.Empty,new Dictionary<string,string>(),new Dictionary<string,string>());throw new Exception("Expected revision refusal.");}catch(InvalidDataException){}
        return Task.CompletedTask;
    }
}
