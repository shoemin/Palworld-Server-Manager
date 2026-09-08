using PalworldServerManager.Core.Authorization;

namespace PalworldServerManager.Core.Operations;

public enum LockRequirement { None = 1, ServerExclusive = 2, HostExclusive = 3 }

// Target describes the work. Lock scope describes exclusion, independently of that target.
public abstract record OperationTarget
{
    public Guid AuthoritativeHostId { get; }
    private protected OperationTarget(Guid hostId)
    {
        if (hostId == Guid.Empty) throw new ArgumentException("An authoritative Host is required.");
        AuthoritativeHostId = hostId;
    }
}

public sealed record HostTarget : OperationTarget
{
    public HostTarget(Guid hostId) : base(hostId) { }
}

public sealed record ServerTarget : OperationTarget
{
    public ServerRef Server { get; }
    public ServerTarget(ServerRef server)
        : base((server ?? throw new ArgumentNullException(nameof(server))).AuthoritativeHostId) => Server = server;
}

public abstract record OperationLockScope
{
    public Guid AuthoritativeHostId { get; }
    private protected OperationLockScope(Guid hostId)
    {
        if (hostId == Guid.Empty) throw new ArgumentException("An authoritative Host is required.");
        AuthoritativeHostId = hostId;
    }
}

public sealed record HostScope : OperationLockScope
{
    public HostScope(Guid hostId) : base(hostId) { }
}

public sealed record ServerScope : OperationLockScope
{
    public ServerRef Server { get; }
    public ServerScope(ServerRef server)
        : base((server ?? throw new ArgumentNullException(nameof(server))).AuthoritativeHostId) => Server = server;
}

public static class OperationLockPolicy
{
    public static OperationLockScope? RequiredScope(OperationTarget target, LockRequirement requirement)
    {
        ValidateTarget(target);
        return requirement switch
        {
            LockRequirement.None => null,
            LockRequirement.HostExclusive => new HostScope(target.AuthoritativeHostId),
            LockRequirement.ServerExclusive when target is ServerTarget server => new ServerScope(server.Server),
            _ => throw new ArgumentException("Unknown or incompatible operation lock requirement.")
        };
    }

    public static bool Conflicts(OperationLockScope left, OperationLockScope right)
    {
        ValidateScope(left); ValidateScope(right);
        return left.AuthoritativeHostId == right.AuthoritativeHostId &&
            (left is HostScope || right is HostScope ||
             left is ServerScope a && right is ServerScope b && a.Server == b.Server);
    }

    // A pure destination check, not authorization or acquisition. The destination repository
    // must check its own current identity and persist record+lock in one transaction.
    public static void RequireAuthoritativeTarget(Guid thisHostId, OperationTarget target)
    {
        ValidateTarget(target);
        if (thisHostId == Guid.Empty || target.AuthoritativeHostId != thisHostId)
            throw new ArgumentException("Operations belong to their authoritative destination Host.");
    }

    private static void ValidateTarget(OperationTarget target)
    {
        if (target is not HostTarget and not ServerTarget) throw new ArgumentException("Unknown operation target.");
    }

    private static void ValidateScope(OperationLockScope scope)
    {
        if (scope is not HostScope and not ServerScope) throw new ArgumentException("Unknown operation lock scope.");
    }
}
