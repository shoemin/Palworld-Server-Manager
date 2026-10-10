using System.Globalization;
using Wire = PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.Contracts;

// Shape/privacy validation only. Neither a valid DTO nor its revision grants authority.
public static class SettingsWireValidation
{
    public const int MaximumEntries = 1024;
    public const int MaximumCharacters = 1_048_576;
    public static bool IsValid(Wire.SettingsSnapshot? snapshot)
    {
        if (snapshot?.Server is null || !Id(snapshot.Server.AuthoritativeHostId) ||
            !Id(snapshot.Server.ServerProfileId) || !Id(snapshot.Revision) || snapshot.Entries.Count > MaximumEntries) return false;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long characters = 0;
        foreach (var entry in snapshot.Entries)
        {
            var d = entry.Definition;
            if (!Definition(d) || !keys.Add(d.Key)) return false;
            characters += d.Key.Length + d.DisplayName.Length + d.Category.Length + d.Description.Length +
                d.Unit.Length + d.Minimum.Length + d.Maximum.Length + d.Step.Length + d.CatalogDefault.Length;
            foreach (var choice in d.Choices) characters += choice.Length;
            foreach (var observation in new[] { entry.Configured, entry.InstallationDefault, entry.Effective })
            {
                if (observation is null) continue;
                if (d.Sensitivity != Wire.SettingsSensitivity.Nonsecret || !Observation(observation)) return false;
                characters += observation.Value.Length;
            }
            if (characters > MaximumCharacters) return false;
        }
        return true;
    }
    public static bool IsValidKey(string? value) => value is { Length: > 0 and <= 256 } &&
        char.IsLetter(value[0]) && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-');
    private static bool Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;
    private static bool Text(string value, int maximum) => value.Length <= maximum && WellFormed(value);
    private static bool WellFormed(string value)
    {
        for (var i = 0; i < value.Length; i++)
            if (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i >= value.Length || !char.IsLowSurrogate(value[i]))) return false;
        return true;
    }
    private static bool Metadata(string value, int maximum) => Text(value, maximum) &&
        !value.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029');
    private static bool Decimal(string value) => value.Length is > 0 and <= 64 && value.Trim() == value &&
        !value.Any(char.IsControl) && decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);
    private static bool Definition(Wire.SettingsDefinition? d)
    {
        if (d is null || !IsValidKey(d.Key) || !Metadata(d.DisplayName, 256) || !Metadata(d.Category, 256) || !Metadata(d.Description, 4096) ||
            d.DisplayName.Length == 0 || d.Category.Length == 0 || (d.HasUnit && !Metadata(d.Unit, 64)) || d.Choices.Count > 128 ||
            d.Choices.Any(c => c.Length == 0 || !Metadata(c, 256)) || d.Choices.Distinct(StringComparer.Ordinal).Count() != d.Choices.Count ||
            (d.HasMinimum && !Decimal(d.Minimum)) || (d.HasMaximum && !Decimal(d.Maximum)) || (d.HasStep && !Decimal(d.Step)) ||
            (d.HasCatalogDefault && !Text(d.CatalogDefault, MaximumCharacters))) return false;
        var expected = d.EditorKind switch
        {
            Wire.SettingsEditorKind.Raw => Wire.SettingsSensitivity.Unknown,
            Wire.SettingsEditorKind.Password => Wire.SettingsSensitivity.Secret,
            Wire.SettingsEditorKind.Boolean or Wire.SettingsEditorKind.Integer or Wire.SettingsEditorKind.Multiplier or
                Wire.SettingsEditorKind.Duration or Wire.SettingsEditorKind.Enum or Wire.SettingsEditorKind.Text or
                Wire.SettingsEditorKind.Compound => Wire.SettingsSensitivity.Nonsecret,
            _ => Wire.SettingsSensitivity.Unspecified
        };
        return expected != Wire.SettingsSensitivity.Unspecified && d.Sensitivity == expected &&
            (d.IsKnown || d.EditorKind == Wire.SettingsEditorKind.Raw) &&
            d.SupportsSemanticEdit == (d.EditorKind is not (Wire.SettingsEditorKind.Raw or Wire.SettingsEditorKind.Compound)) &&
            (expected == Wire.SettingsSensitivity.Nonsecret || !d.HasCatalogDefault) &&
            (d.EditorKind == Wire.SettingsEditorKind.Enum ? d.Choices.Count > 0 : d.Choices.Count == 0);
    }
    private static bool Observation(Wire.SettingsValueObservation observation) => Text(observation.Value, MaximumCharacters) &&
        observation.Validation is Wire.SettingsValidationCode.Valid or Wire.SettingsValidationCode.Unsupported or
            Wire.SettingsValidationCode.BooleanRequired or Wire.SettingsValidationCode.IntegerRequired or
            Wire.SettingsValidationCode.FiniteNumberRequired or Wire.SettingsValidationCode.KnownChoiceRequired or
            Wire.SettingsValidationCode.SingleLineRequired;
}
