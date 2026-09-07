using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

internal static class PeerRotationStatusWire
{
    internal static PeerRotationStatusRequest Wire(RoutineRotationStatusRequest request) => new()
    { QueryId = request.QueryId.ToString("D"), HostId = request.HostId.ToString("D"), RotationId = request.RotationId.ToString("D"), OldFingerprint = request.OldFingerprint, NewFingerprint = request.NewFingerprint };
    internal static RoutineRotationStatusRequest Durable(PeerRotationStatusRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(PeerSecurityRpcService.Id(request.QueryId), PeerSecurityRpcService.Id(request.HostId), PeerSecurityRpcService.Id(request.RotationId), request.OldFingerprint, request.NewFingerprint);
    }
    internal static PeerRotationStatusReply Wire(RoutineRotationStatusReply reply) => new()
    {
        Request = Wire(reply.Request), State = reply.State switch
        {
            RoutineRotationLiveState.Unknown => PeerRotationLiveState.Unknown,
            RoutineRotationLiveState.Staging => PeerRotationLiveState.Staging,
            RoutineRotationLiveState.ReadyForCutover => PeerRotationLiveState.ReadyForCutover,
            RoutineRotationLiveState.CutOver => PeerRotationLiveState.CutOver,
            RoutineRotationLiveState.Completed => PeerRotationLiveState.Completed,
            RoutineRotationLiveState.Aborted => PeerRotationLiveState.Aborted,
            _ => throw new ArgumentException("Unknown rotation status.")
        }
    };
    internal static RoutineRotationStatusReply Durable(PeerRotationStatusReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return new(Durable(reply.Request), reply.State switch
        {
            PeerRotationLiveState.Unknown => RoutineRotationLiveState.Unknown,
            PeerRotationLiveState.Staging => RoutineRotationLiveState.Staging,
            PeerRotationLiveState.ReadyForCutover => RoutineRotationLiveState.ReadyForCutover,
            PeerRotationLiveState.CutOver => RoutineRotationLiveState.CutOver,
            PeerRotationLiveState.Completed => RoutineRotationLiveState.Completed,
            PeerRotationLiveState.Aborted => RoutineRotationLiveState.Aborted,
            _ => throw new ArgumentException("Unknown rotation status.")
        });
    }
}
