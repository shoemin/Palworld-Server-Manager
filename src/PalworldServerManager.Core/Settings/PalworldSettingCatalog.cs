using System.Globalization;
using System.Numerics;
using PalworldServerManager.Core.Services;

namespace PalworldServerManager.Core.Settings;

// Additive v0.5 semantics. The frozen legacy schema remains the source of known
// names/categories/descriptions; a name or prefix alone never infers a value type.
public static class PalworldSettingCatalog
{
    public static PalworldSettingDefinition Resolve(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var legacy=PalworldSettingSchema.Find(key);
        if(legacy is null)return new(key,key,"Advanced / Unknown","Unrecognized setting; preserve its original value.",false);
        var canonical=legacy.Key;
        var (display,kind,unit,choices)=canonical switch
        {
            "bEnableFastTravel"=>("Fast travel",PalworldSettingEditorKind.Boolean,(string?)null,Array.Empty<string>()),
            "ExpRate"=>("Experience gain",PalworldSettingEditorKind.Multiplier,"×",Array.Empty<string>()),
            "DeathPenalty"=>("Death penalty",PalworldSettingEditorKind.Enum,null,new[]{"None","Item","ItemAndEquipment","All"}),
            "AdminPassword"=>("Administrator password",PalworldSettingEditorKind.Password,null,Array.Empty<string>()),
            "ServerPassword"=>("Server password",PalworldSettingEditorKind.Password,null,Array.Empty<string>()),
            "ServerName"=>("Server name",PalworldSettingEditorKind.Text,null,Array.Empty<string>()),
            "ServerDescription"=>("Server description",PalworldSettingEditorKind.Text,null,Array.Empty<string>()),
            "ServerPlayerMaxNum"=>("Maximum players",PalworldSettingEditorKind.Integer,null,Array.Empty<string>()),
            "PalEggDefaultHatchingTime"=>("Huge Egg hatching time",PalworldSettingEditorKind.Duration,"hours",Array.Empty<string>()),
            "CrossplayPlatforms"=>("Crossplay platforms",PalworldSettingEditorKind.Compound,null,Array.Empty<string>()),
            _=>(canonical,PalworldSettingEditorKind.Raw,null,Array.Empty<string>())
        };
        return new(canonical,display,legacy.Category,legacy.Description,true,kind,unit,choices);
    }

    public static SettingValidationResult Validate(string key,string? decodedValue)
    {
        var definition=Resolve(key);var kind=definition.EditorKind;
        // Secrets require a distinct unchanged/replace/clear operation at the Host
        // boundary. A generic text assignment can never implicitly replace or clear one.
        if(kind==PalworldSettingEditorKind.Password)return new(definition.Key,SettingValidationCode.ExplicitSecretActionRequired);
        var code=kind switch
        {
            PalworldSettingEditorKind.Boolean=>decodedValue is not null&&(decodedValue.Equals("True",StringComparison.OrdinalIgnoreCase)||decodedValue.Equals("False",StringComparison.OrdinalIgnoreCase))?SettingValidationCode.Valid:SettingValidationCode.BooleanRequired,
            PalworldSettingEditorKind.Integer=>Integer(decodedValue)?SettingValidationCode.Valid:SettingValidationCode.IntegerRequired,
            PalworldSettingEditorKind.Multiplier or PalworldSettingEditorKind.Duration=>Number(decodedValue)?SettingValidationCode.Valid:SettingValidationCode.FiniteNumberRequired,
            PalworldSettingEditorKind.Enum=>definition.Choices.Contains(decodedValue,StringComparer.Ordinal)?SettingValidationCode.Valid:SettingValidationCode.KnownChoiceRequired,
            PalworldSettingEditorKind.Text=>decodedValue is not null&&!decodedValue.Any(char.IsControl)?SettingValidationCode.Valid:SettingValidationCode.SingleLineRequired,
            _=>SettingValidationCode.Unsupported
        };
        return new(definition.Key,code);
    }

    private static bool Integer(string? value)=>value is not null&&value.Length>0&&value.Trim()==value&&
        BigInteger.TryParse(value,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out _);
    private static bool Number(string? value)=>value is not null&&value.Length>0&&value.Trim()==value&&
        double.TryParse(value,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint|NumberStyles.AllowExponent,CultureInfo.InvariantCulture,out var number)&&double.IsFinite(number);
}
