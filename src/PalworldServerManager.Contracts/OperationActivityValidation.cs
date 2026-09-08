using System.Globalization;
using PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.Contracts;

// Shape checks only. A snapshot is an observation, never authentication or authorization.
// Inconsistent record/lock relationships must remain visible for recovery inspection.
public static class OperationActivityValidation
{
    public static bool IsValid(OperationActivitySnapshot? snapshot)
        => snapshot is not null && Id(snapshot.AuthoritativeHostId) && snapshot.DurableRevision >= 0 &&
            snapshot.Operations.All(o => Item(o, snapshot.AuthoritativeHostId)) &&
            snapshot.Locks.All(l => Lock(l, snapshot.AuthoritativeHostId)) &&
            Unique(snapshot.Operations.Select(o => o.OperationId)) && Unique(snapshot.Locks.Select(l => l.LockId));

    private static bool Item(OperationActivityItem o, string host)
        => Id(o.OperationId) && Name(o.Kind) && Name(o.Phase) && o.RecordRevision >= 0 &&
            Target(o.Target, host) && Utc(o.StartedUtc) && (!o.HasLastHeartbeatUtc || Utc(o.LastHeartbeatUtc)) &&
            (o.Status is OperationActivityStatus.Running or OperationActivityStatus.Resolved or
                OperationActivityStatus.AwaitingRecovery or OperationActivityStatus.RecoveryRequired) &&
            (!o.HasRecovery || o.Recovery is OperationRecoveryDisposition.SafeToRetryFromStart or
                OperationRecoveryDisposition.SafeToResumeFromPhase or OperationRecoveryDisposition.RequiresManualReview or
                OperationRecoveryDisposition.SafeToDiscard);

    private static bool Lock(OperationActivityLock l, string host)
        => Id(l.LockId) && Id(l.OwningOperationId) && Name(l.Kind) && Utc(l.AcquiredUtc) && (l.Scope?.ScopeCase switch
        {
            OperationLockScope.ScopeOneofCase.HostId => SameHost(l.Scope.HostId, host),
            OperationLockScope.ScopeOneofCase.Server => Server(l.Scope.Server, host),
            _ => false
        });
    private static bool Target(OperationTarget? target, string host) => target?.TargetCase switch
    {
        OperationTarget.TargetOneofCase.HostId => SameHost(target.HostId, host),
        OperationTarget.TargetOneofCase.Server => Server(target.Server, host),
        _ => false
    };
    private static bool Server(Wire.ServerRef? server, string host)
        => server is not null && SameHost(server.AuthoritativeHostId, host) && Id(server.ServerProfileId);
    private static bool SameHost(string value, string host) => Id(value) && Guid.Parse(value) == Guid.Parse(host);
    private static bool Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;
    private static bool Unique(IEnumerable<string> ids)
    { var seen = new HashSet<Guid>(); return ids.All(id => seen.Add(Guid.Parse(id))); }
    private static bool Name(string value) => value.Length is > 0 and <= 64 && char.IsAsciiLetter(value[0]) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static bool Utc(string value) => DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var time) && time.Offset == TimeSpan.Zero;
}
