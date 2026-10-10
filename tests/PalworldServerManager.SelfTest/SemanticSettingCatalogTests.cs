using System.Globalization;
using PalworldServerManager.Core.Settings;
using PalworldServerManager.Core.Services;

namespace PalworldServerManager.SelfTest;

internal static class SemanticSettingCatalogTests
{
    private static void Check(bool value){if(!value)throw new Exception("Semantic setting catalog assertion failed.");}
    public static Task EstablishedSemantics()
    {
        var cases=new[]{("bEnableFastTravel",PalworldSettingEditorKind.Boolean),("ExpRate",PalworldSettingEditorKind.Multiplier),
            ("DeathPenalty",PalworldSettingEditorKind.Enum),("ServerPlayerMaxNum",PalworldSettingEditorKind.Integer),
            ("PalEggDefaultHatchingTime",PalworldSettingEditorKind.Duration),("ServerName",PalworldSettingEditorKind.Text),
            ("AdminPassword",PalworldSettingEditorKind.Password),("CrossplayPlatforms",PalworldSettingEditorKind.Compound)};
        foreach(var (key,kind) in cases)
        {
            var definition=PalworldSettingCatalog.Resolve(key.ToLowerInvariant());var legacy=PalworldSettingSchema.Find(key)!;
            Check(definition.IsKnown&&definition.Key==key&&definition.EditorKind==kind&&definition.Category==legacy.Category&&definition.Description==legacy.Description);
            Check(definition.Minimum is null&&definition.Maximum is null&&definition.Step is null&&definition.DefaultValue is null&&definition.RestartRequired is null);
        }
        Check(PalworldSettingCatalog.Resolve("ExpRate").Unit=="×"&&PalworldSettingCatalog.Resolve("PalEggDefaultHatchingTime").Unit=="hours");
        return Task.CompletedTask;
    }
    public static Task UnknownAndUnclassifiedAreNotGuessed()
    {
        foreach(var key in new[]{"bFutureSecret","FutureRate","UnknownPath","RandomizerSeed","AllowConnectPlatform"})
        {
            var definition=PalworldSettingCatalog.Resolve(key);
            Check(definition.EditorKind==PalworldSettingEditorKind.Raw&&!definition.SupportsSemanticEdit&&definition.Unit is null&&definition.Choices.Count==0&&definition.IsSecret is null);
            Check(PalworldSettingCatalog.Validate(key,"anything").Code==SettingValidationCode.Unsupported);
        }
        Check(!PalworldSettingCatalog.Resolve("bFutureSecret").IsKnown&&PalworldSettingCatalog.Resolve("RandomizerSeed").IsKnown);
        var compound=PalworldSettingCatalog.Resolve("CrossplayPlatforms");
        Check(!compound.SupportsSemanticEdit&&compound.Choices.Count==0&&PalworldSettingCatalog.Validate(compound.Key,"(FuturePlatform)").Code==SettingValidationCode.Unsupported);
        return Task.CompletedTask;
    }
    public static Task InvariantNumericValidation()
    {
        var old=CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
            foreach(var value in new[]{"1.25","-3","1e3","+2.0"})Check(PalworldSettingCatalog.Validate("ExpRate",value).IsValid);
            foreach(var value in new string?[]{null,"","two","1,25","NaN","Infinity","1e999"," 2","2 ","1\0","1\0\0","1\n"})Check(PalworldSettingCatalog.Validate("ExpRate",value).Code==SettingValidationCode.FiniteNumberRequired);
            Check(PalworldSettingCatalog.Validate("ServerPlayerMaxNum","999999999999999999999999999999").IsValid);
            Check(PalworldSettingCatalog.Validate("ServerPlayerMaxNum","-3").IsValid); // No invented domain minimum.
            foreach(var value in new[]{"1.5","1e3","1,000","","1\0","1\0\0","1\n"})Check(PalworldSettingCatalog.Validate("ServerPlayerMaxNum",value).Code==SettingValidationCode.IntegerRequired);
            Check(PalworldSettingCatalog.Validate("PalEggDefaultHatchingTime","0.5").IsValid);
        }
        finally{CultureInfo.CurrentCulture=old;}
        return Task.CompletedTask;
    }
    public static Task ExplicitBooleanEnumAndText()
    {
        Check(PalworldSettingCatalog.Validate("bEnableFastTravel","true").IsValid&&PalworldSettingCatalog.Validate("bEnableFastTravel","FALSE").IsValid);
        foreach(var value in new[]{"1","yes"," True",""})Check(PalworldSettingCatalog.Validate("bEnableFastTravel",value).Code==SettingValidationCode.BooleanRequired);
        foreach(var value in new[]{"None","Item","ItemAndEquipment","All"})Check(PalworldSettingCatalog.Validate("DeathPenalty",value).IsValid);
        foreach(var value in new[]{"FutureChoice","none",""})Check(PalworldSettingCatalog.Validate("DeathPenalty",value).Code==SettingValidationCode.KnownChoiceRequired);
        Check(PalworldSettingCatalog.Validate("ServerName","世界, \"quoted\" (name)").IsValid&&PalworldSettingCatalog.Validate("ServerDescription","").IsValid);
        foreach(var key in new[]{"ServerName","ServerDescription"})
        foreach(var value in new[]{"x\ny","x\ry","x\0y","x\u2028y","x\u2029y"})
            Check(PalworldSettingCatalog.Validate(key,value).Code==SettingValidationCode.SingleLineRequired);
        return Task.CompletedTask;
    }
    public static Task SecretErrorsNeverRetainValues()
    {
        foreach(var key in new[]{"AdminPassword","ServerPassword"})foreach(var value in new string?[]{null,"","private-test-value"})
        {
            var definition=PalworldSettingCatalog.Resolve(key);var result=PalworldSettingCatalog.Validate(key,value);
            Check(definition.IsSecret==true&&definition.DefaultValue is null&&result.Code==SettingValidationCode.ExplicitSecretActionRequired);
            Check(!result.ToString().Contains("private-test-value",StringComparison.Ordinal));
        }
        Check(PalworldSettingCatalog.Resolve("ExpRate").IsSecret==false);
        var invalid=PalworldSettingCatalog.Validate("ExpRate","private-test-value");
        Check(!invalid.ToString().Contains("private-test-value",StringComparison.Ordinal));return Task.CompletedTask;
    }
    public static Task MetadataIsImmutable()
    {
        var definition=PalworldSettingCatalog.Resolve("DeathPenalty");
        Check(definition.Choices.SequenceEqual(new[]{"None","Item","ItemAndEquipment","All"}));
        try{((IList<string>)definition.Choices)[0]="changed";throw new Exception("Mutable semantic choices.");}catch(NotSupportedException){}
        Check(PalworldSettingCatalog.Resolve("DeathPenalty").Choices[0]=="None");
        foreach(var property in typeof(PalworldSettingDefinition).GetProperties())Check(property.SetMethod is null);
        return Task.CompletedTask;
    }
}
