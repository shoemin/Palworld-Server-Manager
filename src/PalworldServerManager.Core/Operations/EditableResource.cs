namespace PalworldServerManager.Core.Operations;

// A code-defined editable aggregate at an explicit qualified destination. This identity is
// not a filesystem path, permission grant or client-defined operation registration.
public sealed record EditableResource
{
    public string Kind { get; }
    public OperationTarget Target { get; }
    public EditableResource(string kind, OperationTarget target)
    {
        Kind = OperationDefinition.RequireName(kind);
        ArgumentNullException.ThrowIfNull(target);
        OperationLockPolicy.RequireAuthoritativeTarget(target.AuthoritativeHostId, target);
        Target = target;
    }
}
