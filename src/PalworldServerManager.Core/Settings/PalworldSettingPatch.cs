using PalworldServerManager.Core.Services;

namespace PalworldServerManager.Core.Settings;

public enum SettingEditAction { Unchanged = 1, SetValue = 2, ReplaceSecret = 3, ClearSecret = 4 }

// Not a record: default diagnostics must never format the replacement text.
public sealed class PalworldSettingEdit
{
    public string Key { get; }
    public SettingEditAction Action { get; }
    internal string? Value { get; }
    private PalworldSettingEdit(string key, SettingEditAction action, string? value) { Key=key;Action=action;Value=value; }
    public static PalworldSettingEdit Unchanged(string key) => new(key,SettingEditAction.Unchanged,null);
    public static PalworldSettingEdit Set(string key,string value) => new(key,SettingEditAction.SetValue,value);
    public static PalworldSettingEdit ReplaceSecret(string key,string value) => new(key,SettingEditAction.ReplaceSecret,value);
    public static PalworldSettingEdit ClearSecret(string key) => new(key,SettingEditAction.ClearSecret,null);
}
public enum SettingPatchCode { InvalidRequest, DuplicateEdit, DuplicateSource, UnsupportedSetting, WrongAction, InvalidValue, EmptySecretReplacement, UnsupportedRepresentation }
// Index into the submitted edit list; no input key or value is echoed.
public sealed record SettingPatchError(int EditIndex,SettingPatchCode Code,SettingValidationCode? Validation = null);
public sealed class SettingPatchResult
{
    public PreservedPalworldConfigFile? Output { get; }
    public IReadOnlyList<SettingPatchError> Errors { get; }
    public bool IsValid => Output is not null;
    internal SettingPatchResult(PreservedPalworldConfigFile? output,IEnumerable<SettingPatchError> errors)
    { Output=output;Errors=Array.AsReadOnly(errors.ToArray()); }
}

// Pure preparation only. No path, IO, authority, version check or stopped-state claim.
public static class PalworldSettingPatch
{
    public const int MaximumEdits=1024;
    public static SettingPatchResult Prepare(PreservedPalworldConfigFile source,IReadOnlyList<PalworldSettingEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(edits);
        if(edits.Count>MaximumEdits)return Failure(-1,SettingPatchCode.InvalidRequest);
        var inputs=edits.ToArray();long size=0;
        for(var i=0;i<inputs.Length;i++)
        {
            var edit=inputs[i];
            if(edit is null||!Key(edit.Key)||(size+=(long)edit.Key.Length+(edit.Value?.Length??0))>PreservedPalworldConfigDocument.MaximumCharacters)
                return Failure(i,SettingPatchCode.InvalidRequest);
        }
        var duplicates=source.Keys.GroupBy(k=>k,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var changes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var errors=new List<SettingPatchError>();
        for(var i=0;i<inputs.Length;i++)
        {
            var edit=inputs[i];
            if(!seen.Add(edit.Key)){errors.Add(new(i,SettingPatchCode.DuplicateEdit));continue;}
            if(edit.Action==SettingEditAction.Unchanged)continue;
            if(duplicates.Contains(edit.Key)){errors.Add(new(i,SettingPatchCode.DuplicateSource));continue;}
            var d=PalworldSettingCatalog.Resolve(edit.Key);
            if(!d.SupportsSemanticEdit){errors.Add(new(i,SettingPatchCode.UnsupportedSetting));continue;}
            if(d.IsSecret==true)
            {
                if(edit.Action==SettingEditAction.ClearSecret){changes.Add(d.Key,"\"\"");continue;}
                if(edit.Action!=SettingEditAction.ReplaceSecret){errors.Add(new(i,SettingPatchCode.WrongAction,SettingValidationCode.ExplicitSecretActionRequired));continue;}
                if(string.IsNullOrEmpty(edit.Value)){errors.Add(new(i,SettingPatchCode.EmptySecretReplacement));continue;}
            }
            else
            {
                if(edit.Action!=SettingEditAction.SetValue){errors.Add(new(i,SettingPatchCode.WrongAction));continue;}
                var validation=PalworldSettingCatalog.Validate(d.Key,edit.Value);
                if(!validation.IsValid){errors.Add(new(i,SettingPatchCode.InvalidValue,validation.Code));continue;}
            }
            if(edit.Value is null||!Representable(edit.Value)){errors.Add(new(i,SettingPatchCode.UnsupportedRepresentation));continue;}
            var value=d.EditorKind switch
            {
                PalworldSettingEditorKind.Text or PalworldSettingEditorKind.Password=>PalworldConfigParser.Quote(edit.Value),
                PalworldSettingEditorKind.Boolean=>edit.Value.Equals("True",StringComparison.OrdinalIgnoreCase)?"True":"False",
                _=>edit.Value
            };
            changes.Add(d.Key,value);
        }
        if(errors.Count>0)return new(null,errors);
        try{return new(source.WithValues(changes),Array.Empty<SettingPatchError>());}
        catch(FormatException){return Failure(-1,SettingPatchCode.UnsupportedRepresentation);}
        catch(InvalidOperationException){return Failure(-1,SettingPatchCode.DuplicateSource);}
    }
    private static SettingPatchResult Failure(int index,SettingPatchCode code)=>new(null,new[]{new SettingPatchError(index,code)});
    private static bool Key(string? key)=>key is { Length:>0 and <=256 }&&(char.IsLetter(key[0])||key[0]=='_')&&key.All(c=>char.IsLetterOrDigit(c)||c is '_' or '.' or '-');
    private static bool Representable(string text)
    {
        // The accepted preserved-value writer refuses controls. No game password range is invented.
        for(var i=0;i<text.Length;i++)
        {
            if(char.IsControl(text[i]))return false;
            if(char.IsSurrogate(text[i])&&(!char.IsHighSurrogate(text[i])||++i>=text.Length||!char.IsLowSurrogate(text[i])))return false;
        }
        return true;
    }
}
