using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Operations;

namespace PalworldServerManager.Host.Persistence;

public sealed partial class OperationRepository
{
    public DurableOperation Start(Guid operationId, string kind, OperationTarget target,
        Action<SqliteConnection, SqliteTransaction> requireCurrentAuthority, CancellationToken ct = default)
    {
        Id(operationId); ArgumentNullException.ThrowIfNull(kind); ArgumentNullException.ThrowIfNull(requireCurrentAuthority);
        OperationLockPolicy.RequireAuthoritativeTarget(hostId, target); ct.ThrowIfCancellationRequested();
        if (!definitions.TryGetValue(kind, out var definition)) throw new ArgumentException("Unknown trusted operation kind.");
        var scope = OperationLockPolicy.RequiredScope(target, definition.LockRequirement);
        using var c = Open(false); using var tx = c.BeginTransaction(deferred: false);
        RequireHost(c, tx); requireCurrentAuthority(c, tx); var before = Read(c, tx); RequireConsistent(before);
        if (before.Operations.Any(o => o.Operation.OperationId == operationId) ||
            scope is not null && before.Locks.Any(l => OperationLockPolicy.Conflicts(l.Scope, scope))) throw new OperationConflictException();
        var now = Now(); var phase = definition.GetPhase(definition.InitialPhase);
        var record = new DurableOperation(operationId, kind, target, phase.Name, false, phase.Recovery,
            definition.LockRequirement, definition.Fingerprint, 1, now, now);
        var expectedRevision = checked(before.Revision + (scope is null ? 1 : 2));
        ct.ThrowIfCancellationRequested();
        Execute(c, tx, """
            INSERT INTO OperationRecords (OperationId,Kind,TargetKind,TargetHostId,TargetServerProfileId,Phase,IsTerminal,
                RecoveryDisposition,StartedUtc,LastHeartbeatUtc,LockRequirement,PolicyFingerprint,RecordRevision)
            VALUES ($id,$kind,$target,$host,$server,$phase,0,$recovery,$now,$now,$lock,$policy,1);
            """, ("$id", Id(operationId)), ("$kind", kind), ("$target", target is HostTarget ? "HostTarget" : "ServerTarget"),
            ("$host", Id(hostId)), ("$server", (target as ServerTarget)?.Server.ServerProfileId.ToString("D")),
            ("$phase", phase.Name), ("$recovery", phase.Recovery!.Value.ToString()), ("$now", Stamp(now)),
            ("$lock", definition.LockRequirement.ToString()), ("$policy", definition.Fingerprint));
        DurableOperationLock? createdLock = null;
        if (scope is not null)
        {
            createdLock = new(Guid.NewGuid(), scope, kind, operationId, now);
            Execute(c, tx, """
                INSERT INTO OperationLocks (OperationLockId,ScopeKind,ScopeHostId,ScopeServerProfileId,OperationKind,OwningOperationId,AcquiredUtc)
                    VALUES ($lockId,$scope,$host,$server,$kind,$id,$now);
                """, ("$lockId", Id(createdLock.LockId)), ("$scope", scope is HostScope ? "HostScope" : "ServerScope"),
                ("$host", Id(hostId)), ("$server", (scope as ServerScope)?.Server.ServerProfileId.ToString("D")),
                ("$kind", kind), ("$id", Id(operationId)), ("$now", Stamp(now)));
        }
        requireCurrentAuthority(c, tx); RequireHost(c, tx); var after = Read(c, tx); RequireConsistent(after);
        if (after.Revision != expectedRevision || after.Operations.Count != before.Operations.Count + 1 ||
            after.Operations.Single(o => o.Operation.OperationId == operationId).Operation != record ||
            after.Locks.Count != before.Locks.Count + (scope is null ? 0 : 1) ||
            createdLock is not null && after.Locks.Single(l => l.LockId == createdLock.LockId) != createdLock)
            throw new InvalidOperationException("Operation admission changed before commit.");
        ct.ThrowIfCancellationRequested(); tx.Commit(); return record;
    }

    public DurableOperation Transition(Guid operationId, long expectedRevision, string nextPhase,
        Action<SqliteConnection, SqliteTransaction> requireCurrentAuthority, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nextPhase);
        return Update(operationId, expectedRevision, nextPhase, requireCurrentAuthority, ct);
    }

    public DurableOperation Heartbeat(Guid operationId, long expectedRevision,
        Action<SqliteConnection, SqliteTransaction> requireCurrentAuthority, CancellationToken ct = default)
        => Update(operationId, expectedRevision, null, requireCurrentAuthority, ct);

    private DurableOperation Update(Guid operationId, long expectedRevision, string? nextPhase,
        Action<SqliteConnection, SqliteTransaction> requireCurrentAuthority, CancellationToken ct)
    {
        Id(operationId); ArgumentNullException.ThrowIfNull(requireCurrentAuthority); ct.ThrowIfCancellationRequested();
        if (expectedRevision < 1) throw new StaleWriteConflictException();
        using var c = Open(false); using var tx = c.BeginTransaction(deferred: false);
        RequireHost(c, tx); requireCurrentAuthority(c, tx); var before = Read(c, tx); RequireConsistent(before);
        var op = before.Operations.SingleOrDefault(o => o.Operation.OperationId == operationId)?.Operation
            ?? throw new OperationConflictException();
        if (op.Revision != expectedRevision) throw new StaleWriteConflictException();
        if (op.IsTerminal) throw new OperationConflictException();
        var definition = definitions[op.Kind];
        if (nextPhase is not null && !definition.CanTransition(op.Phase, nextPhase)) throw new ArgumentException("Undeclared operation phase transition.");
        var phase = definition.GetPhase(nextPhase ?? op.Phase); var now = Now();
        var previousHeartbeat = op.LastHeartbeatUtc ?? op.StartedUtc;
        if (now < previousHeartbeat) now = previousHeartbeat;
        var changed = op with { Phase = phase.Name, IsTerminal = phase.IsTerminal, Recovery = phase.Recovery,
            Revision = checked(op.Revision + 1), LastHeartbeatUtc = now };
        var released = phase.IsTerminal ? before.Locks.Count(l => l.OwningOperationId == operationId) : 0;
        var expectedStateRevision = checked(before.Revision + 1 + released);
        ct.ThrowIfCancellationRequested();
        if (Execute(c, tx, """
            UPDATE OperationRecords SET Phase=$phase,IsTerminal=$terminal,RecoveryDisposition=$recovery,
                LastHeartbeatUtc=$now,RecordRevision=$next WHERE OperationId=$id AND RecordRevision=$expected AND IsTerminal=0;
            """, ("$phase", phase.Name), ("$terminal", phase.IsTerminal ? 1 : 0), ("$recovery", phase.Recovery?.ToString()),
            ("$now", Stamp(now)), ("$next", changed.Revision), ("$id", Id(operationId)), ("$expected", expectedRevision)) != 1)
            throw new StaleWriteConflictException();
        if (phase.IsTerminal && Execute(c, tx, "DELETE FROM OperationLocks WHERE OwningOperationId=$id;", ("$id", Id(operationId))) != released)
            throw new InvalidOperationException("Operation lock release changed.");
        requireCurrentAuthority(c, tx); RequireHost(c, tx); var after = Read(c, tx); RequireConsistent(after);
        if (after.Revision != expectedStateRevision || after.Operations.Count != before.Operations.Count ||
            after.Operations.Single(o => o.Operation.OperationId == operationId).Operation != changed ||
            after.Locks.Count != before.Locks.Count - released ||
            (!phase.IsTerminal && !after.Locks.SequenceEqual(before.Locks)))
            throw new InvalidOperationException("Operation transition changed before commit.");
        ct.ThrowIfCancellationRequested(); tx.Commit(); return changed;
    }

    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.Offset == TimeSpan.Zero ? now : throw new InvalidOperationException("UTC time required.");
    }
}
