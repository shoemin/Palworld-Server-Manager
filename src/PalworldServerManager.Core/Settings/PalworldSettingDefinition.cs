using System.Collections.ObjectModel;

namespace PalworldServerManager.Core.Settings;

public enum PalworldSettingEditorKind { Raw=0, Boolean=1, Integer=2, Multiplier=3, Duration=4, Enum=5, Text=6, Password=7, Compound=8 }
public enum SettingValidationCode { Valid=0, Unsupported=1, BooleanRequired=2, IntegerRequired=3, FiniteNumberRequired=4, KnownChoiceRequired=5, SingleLineRequired=6, ExplicitSecretActionRequired=7 }

// Semantic input is decoded text, not an INI expression. These errors never retain a value.
public sealed record SettingValidationResult(string Key,SettingValidationCode Code)
{
    public bool IsValid=>Code==SettingValidationCode.Valid;
}

public sealed class PalworldSettingDefinition
{
    public string Key{get;}
    public string DisplayName{get;}
    public string Category{get;}
    public string Description{get;}
    public PalworldSettingEditorKind EditorKind{get;}
    public bool IsKnown{get;}
    public bool IsSecret=>EditorKind==PalworldSettingEditorKind.Password;
    public bool SupportsSemanticEdit=>EditorKind is not (PalworldSettingEditorKind.Raw or PalworldSettingEditorKind.Compound);
    public string? Unit{get;}
    public IReadOnlyList<string> Choices{get;}
    // No authoritative bounds/default/restart data is established by the baseline catalog.
    public decimal? Minimum=>null;
    public decimal? Maximum=>null;
    public decimal? Step=>null;
    public string? DefaultValue=>null;
    public bool? RestartRequired=>null;

    internal PalworldSettingDefinition(string key,string display,string category,string description,bool known,
        PalworldSettingEditorKind kind=PalworldSettingEditorKind.Raw,string? unit=null,params string[] choices)
    {
        Key=key;DisplayName=display;Category=category;Description=description;IsKnown=known;
        EditorKind=kind;Unit=unit;Choices=new ReadOnlyCollection<string>(choices.ToArray());
    }
}
