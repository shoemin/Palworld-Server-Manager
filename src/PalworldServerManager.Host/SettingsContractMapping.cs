using System.Globalization;
using PalworldServerManager.Contracts;
using PalworldServerManager.Core.Settings;
using Wire = PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.Host;

// Trusted Host-only decoded inputs. This is not a file reader, revision issuer or authorization boundary.
internal static class SettingsContractMapping
{
    public static Wire.SettingsSnapshot Snapshot(ServerRef server, long revision,
        IReadOnlyDictionary<string, string> configured, IReadOnlyDictionary<string, string> installationDefaults)
    {
        ArgumentNullException.ThrowIfNull(server);
        var active = Copy(configured); var defaults = Copy(installationDefaults);
        var result = new Wire.SettingsSnapshot { Server = server.ToWire(), Revision = revision.ToString(CultureInfo.InvariantCulture) };
        foreach (var key in active.Keys.Union(defaults.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var definition = PalworldSettingCatalog.Resolve(key);
            var entry = new Wire.SettingsEntry { Definition = Definition(definition) };
            // Positive nonsecret classification is required for every source, before validation.
            if (definition.IsSecret == false)
            {
                if (active.TryGetValue(key, out var value)) entry.Configured = Observation(definition.Key, value);
                if (defaults.TryGetValue(key, out value)) entry.InstallationDefault = Observation(definition.Key, value);
            }
            result.Entries.Add(entry);
        }
        if (!SettingsWireValidation.IsValid(result)) throw Invalid();
        return result;
    }
    private static Dictionary<string, string> Copy(IReadOnlyDictionary<string, string> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count > SettingsWireValidation.MaximumEntries) throw Invalid();
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var pair in source)
        {
            if (!SettingsWireValidation.IsValidKey(pair.Key) || pair.Value is null ||
                (size += pair.Key.Length + pair.Value.Length) > SettingsWireValidation.MaximumCharacters ||
                !copy.TryAdd(pair.Key, pair.Value)) throw Invalid();
        }
        return copy;
    }
    private static Wire.SettingsDefinition Definition(PalworldSettingDefinition d)
    {
        var result = new Wire.SettingsDefinition
        {
            Key = d.Key, DisplayName = d.DisplayName, Category = d.Category, Description = d.Description,
            IsKnown = d.IsKnown, SupportsSemanticEdit = d.SupportsSemanticEdit,
            Sensitivity = d.IsSecret switch { true => Wire.SettingsSensitivity.Secret, false => Wire.SettingsSensitivity.Nonsecret, null => Wire.SettingsSensitivity.Unknown },
            EditorKind = d.EditorKind switch
            {
                PalworldSettingEditorKind.Raw => Wire.SettingsEditorKind.Raw,
                PalworldSettingEditorKind.Boolean => Wire.SettingsEditorKind.Boolean,
                PalworldSettingEditorKind.Integer => Wire.SettingsEditorKind.Integer,
                PalworldSettingEditorKind.Multiplier => Wire.SettingsEditorKind.Multiplier,
                PalworldSettingEditorKind.Duration => Wire.SettingsEditorKind.Duration,
                PalworldSettingEditorKind.Enum => Wire.SettingsEditorKind.Enum,
                PalworldSettingEditorKind.Text => Wire.SettingsEditorKind.Text,
                PalworldSettingEditorKind.Password => Wire.SettingsEditorKind.Password,
                PalworldSettingEditorKind.Compound => Wire.SettingsEditorKind.Compound,
                _ => throw Invalid()
            }
        };
        result.Choices.AddRange(d.Choices);
        if (d.Unit is not null) result.Unit = d.Unit;
        if (d.Minimum is { } min) result.Minimum = min.ToString(CultureInfo.InvariantCulture);
        if (d.Maximum is { } max) result.Maximum = max.ToString(CultureInfo.InvariantCulture);
        if (d.Step is { } step) result.Step = step.ToString(CultureInfo.InvariantCulture);
        if (d.IsSecret == false && d.DefaultValue is not null) result.CatalogDefault = d.DefaultValue;
        if (d.RestartRequired is { } restart) result.RestartRequired = restart;
        return result;
    }
    private static Wire.SettingsValueObservation Observation(string key, string value) => new()
    {
        Value = value,
        Validation = PalworldSettingCatalog.Validate(key, value).Code switch
        {
            SettingValidationCode.Valid => Wire.SettingsValidationCode.Valid,
            SettingValidationCode.Unsupported => Wire.SettingsValidationCode.Unsupported,
            SettingValidationCode.BooleanRequired => Wire.SettingsValidationCode.BooleanRequired,
            SettingValidationCode.IntegerRequired => Wire.SettingsValidationCode.IntegerRequired,
            SettingValidationCode.FiniteNumberRequired => Wire.SettingsValidationCode.FiniteNumberRequired,
            SettingValidationCode.KnownChoiceRequired => Wire.SettingsValidationCode.KnownChoiceRequired,
            SettingValidationCode.SingleLineRequired => Wire.SettingsValidationCode.SingleLineRequired,
            _ => throw Invalid()
        }
    };
    private static InvalidDataException Invalid() => new("Settings observation cannot be represented safely.");
}
