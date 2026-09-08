using System.Globalization;
using PalworldServerManager.Contracts;
using PalworldServerManager.Core.Operations;
using Wire = PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.Host;

// Trusted mapping only, not a read-authority policy or RPC. The publisher must establish
// current caller authority and permitted visibility before obtaining/publishing any state.
internal static class HostOperationActivity
{
    public static Wire.OperationActivitySnapshot ToWire(Guid hostId, HostOperationRuntimeSnapshot state, NegotiatedProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(protocol);
        protocol.Require(Wire.FeatureCapability.OperationActivity);
        var result = new Wire.OperationActivitySnapshot { AuthoritativeHostId = hostId.ToString("D"),
            DurableRevision = state.DurableState.Revision, HasUnqualifiedState = state.DurableState.HasUnqualifiedState };
        foreach (var observation in state.Operations)
        {
            var op = observation.Operation;
            var item = new Wire.OperationActivityItem { OperationId = op.OperationId.ToString("D"), Kind = op.Kind,
                Target = op.Target switch {
                    HostTarget h => new() { HostId = h.AuthoritativeHostId.ToString("D") },
                    ServerTarget s => new() { Server = Server(s.Server) }, _ => throw Invalid() },
                Phase = op.Phase, IsTerminal = op.IsTerminal, RecordRevision = op.Revision, StartedUtc = Stamp(op.StartedUtc),
                Status = observation.Status switch {
                    HostOperationStatus.Running => Wire.OperationActivityStatus.Running,
                    HostOperationStatus.Resolved => Wire.OperationActivityStatus.Resolved,
                    HostOperationStatus.AwaitingRecovery => Wire.OperationActivityStatus.AwaitingRecovery,
                    HostOperationStatus.RecoveryRequired => Wire.OperationActivityStatus.RecoveryRequired, _ => throw Invalid() } };
            if (op.LastHeartbeatUtc is { } heartbeat) item.LastHeartbeatUtc = Stamp(heartbeat);
            if (op.Recovery is { } recovery) item.Recovery = recovery switch {
                RecoveryDisposition.SafeToRetryFromStart => Wire.OperationRecoveryDisposition.SafeToRetryFromStart,
                RecoveryDisposition.SafeToResumeFromPhase => Wire.OperationRecoveryDisposition.SafeToResumeFromPhase,
                RecoveryDisposition.RequiresManualReview => Wire.OperationRecoveryDisposition.RequiresManualReview,
                RecoveryDisposition.SafeToDiscard => Wire.OperationRecoveryDisposition.SafeToDiscard, _ => throw Invalid() };
            result.Operations.Add(item);
        }
        foreach (var held in state.DurableState.Locks)
            result.Locks.Add(new Wire.OperationActivityLock { LockId = held.LockId.ToString("D"), Kind = held.Kind,
                OwningOperationId = held.OwningOperationId.ToString("D"), AcquiredUtc = Stamp(held.AcquiredUtc),
                Scope = held.Scope switch {
                    HostScope h => new() { HostId = h.AuthoritativeHostId.ToString("D") },
                    ServerScope s => new() { Server = Server(s.Server) }, _ => throw Invalid() } });
        if (!OperationActivityValidation.IsValid(result)) throw Invalid();
        return result;
    }
    private static Wire.ServerRef Server(Core.Authorization.ServerRef s)
        => new() { AuthoritativeHostId = s.AuthoritativeHostId.ToString("D"), ServerProfileId = s.ServerProfileId.ToString("D") };
    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static InvalidDataException Invalid() => new("Operation observations cannot be represented safely.");
}
