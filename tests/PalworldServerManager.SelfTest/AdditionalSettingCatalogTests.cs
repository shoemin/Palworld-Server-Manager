using PalworldServerManager.Core.Settings;
using PalworldServerManager.Core.Services;
namespace PalworldServerManager.SelfTest;
internal static class AdditionalSettingCatalogTests
{
    private static void Check(bool value){if(!value)throw new Exception("Additional setting semantics assertion failed.");}
    public static Task KnownKindsAndUnits()
    {
        foreach(var key in new[]{"bAllowClientMod","RCONEnabled","RESTAPIEnabled","bAllowGlobalPalboxExport","bIsPvP","bPalLost"})
        {
            var d=PalworldSettingCatalog.Resolve(key.ToLowerInvariant());
            Check(d.Key==key&&d.EditorKind==PalworldSettingEditorKind.Boolean&&d.IsSecret==false&&d.SupportsSemanticEdit);
            Check(PalworldSettingCatalog.Validate(key,"False").IsValid&&!PalworldSettingCatalog.Validate(key,"yes").IsValid);
        }
        foreach(var key in new[]{"BaseCampMaxNum","PublicPort","RESTAPIPort","GuildPlayerMaxNum"})
        {
            var d=PalworldSettingCatalog.Resolve(key);Check(d.EditorKind==PalworldSettingEditorKind.Integer);
            Check(PalworldSettingCatalog.Validate(key,"123").IsValid&&!PalworldSettingCatalog.Validate(key,"1.5").IsValid);
            Check(d.Minimum is null&&d.Maximum is null&&d.Step is null);
        }
        foreach(var key in new[]{"BuildObjectDamageRate","CollectionObjectRespawnSpeedRate","ItemCorruptionMultiplier","RespawnPenaltyTimeScale"})
        {
            var d=PalworldSettingCatalog.Resolve(key);Check(d.EditorKind==PalworldSettingEditorKind.Multiplier&&d.Unit=="×");
            Check(PalworldSettingCatalog.Validate(key,"1.25").IsValid&&!PalworldSettingCatalog.Validate(key,"Infinity").IsValid);
        }
        foreach(var (key,unit) in new[]{("ItemContainerForceMarkDirtyInterval","seconds"),("BlockRespawnTime","seconds"),("GuildRejoinCooldownMinutes","minutes"),("SupplyDropSpan","minutes")})
        {
            var d=PalworldSettingCatalog.Resolve(key);Check(d.EditorKind==PalworldSettingEditorKind.Duration&&d.Unit==unit);
            Check(PalworldSettingCatalog.Validate(key,"0.5").IsValid&&d.DefaultValue is null&&d.RestartRequired is null);
        }
        return Task.CompletedTask;
    }
    public static Task ExplicitChoicesAndUnclassifiedBoundaries()
    {
        foreach(var (key,choices) in new[]{("LogFormatType",new[]{"Text","Json"}),("RandomizerType",new[]{"None","Region","All"})})
        {
            var d=PalworldSettingCatalog.Resolve(key);Check(d.Choices.SequenceEqual(choices)&&d.EditorKind==PalworldSettingEditorKind.Enum);
            foreach(var choice in choices)Check(PalworldSettingCatalog.Validate(key,choice).IsValid);
            Check(!PalworldSettingCatalog.Validate(key,"Future").IsValid&&!PalworldSettingCatalog.Validate(key,choices[0].ToLowerInvariant()).IsValid);
            try{((IList<string>)d.Choices)[0]="changed";throw new Exception("Mutable choices.");}catch(NotSupportedException){}
        }
        foreach(var key in new[]{"bFutureFeature","FutureRate","RandomizerSeed","AllowConnectPlatform","VoiceChatMaxVolumeDistance","AutoResetGuildTimeNoOnlinePlayers","PalAutoHpRegeneRateInSleep"})
        {
            var d=PalworldSettingCatalog.Resolve(key);
            Check(d.EditorKind==PalworldSettingEditorKind.Raw&&!d.SupportsSemanticEdit&&d.IsSecret is null&&d.Unit is null&&d.Choices.Count==0);
            Check(PalworldSettingCatalog.Validate(key,"1").Code==SettingValidationCode.Unsupported);
            if(PalworldSettingSchema.Find(key) is {} legacy)Check(d.Description==legacy.Description&&d.Category==legacy.Category);
        }
        return Task.CompletedTask;
    }
}
