namespace PalworldServerManager.Core.Operations;

// Observations only: repositories always re-read authoritative state before mutation.
// Nullable policy metadata denotes unqualified historical work, never LockRequirement.None.
public sealed record DurableOperation(Guid OperationId, string Kind, OperationTarget Target, string Phase,
    bool IsTerminal, RecoveryDisposition? Recovery, LockRequirement? LockRequirement, string? PolicyFingerprint,
    long Revision, DateTimeOffset StartedUtc, DateTimeOffset? LastHeartbeatUtc);

public sealed record DurableOperationLock(Guid LockId, OperationLockScope Scope, string Kind,
    Guid OwningOperationId, DateTimeOffset AcquiredUtc);

public sealed record OperationObservation(DurableOperation Operation, bool PolicyIsCurrent);

public sealed class OperationStateSnapshot
{
    public long Revision { get; }
    public IReadOnlyList<OperationObservation> Operations { get; }
    public IReadOnlyList<DurableOperationLock> Locks { get; }
    public bool HasUnqualifiedState { get; }
    public OperationStateSnapshot(long revision, IEnumerable<OperationObservation> operations,
        IEnumerable<DurableOperationLock> locks, bool hasUnqualifiedState)
    {
        Revision = revision; Operations = Array.AsReadOnly(operations.ToArray());
        Locks = Array.AsReadOnly(locks.ToArray()); HasUnqualifiedState = hasUnqualifiedState;
    }
}
