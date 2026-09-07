using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public enum RoutineRotationLiveState { Unknown = 1, Staging = 2, ReadyForCutover = 3, CutOver = 4, Completed = 5, Aborted = 6 }
// Public correlation data, not evidence of a live connection or key possession.
public sealed record RoutineRotationStatusRequest(Guid QueryId, Guid HostId, Guid RotationId, string OldFingerprint, string NewFingerprint);
public sealed record RoutineRotationStatusReply(RoutineRotationStatusRequest Request, RoutineRotationLiveState State);

public sealed partial class HostCredentialStateRepository
{
    // Trusted adapter independently supplies the actual local credential of this completed TLS
    // connection and authenticates the requesting Active peer before exposing this read.
    public RoutineRotationStatusReply ReadRoutineRotationStatus(RoutineRotationStatusRequest request, string actualLocalFingerprint)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.HostId != _hostId || request.QueryId == Guid.Empty || request.RotationId == Guid.Empty ||
            !HostTrustPlanning.Fingerprint(request.OldFingerprint) || !HostTrustPlanning.Fingerprint(request.NewFingerprint) ||
            request.OldFingerprint == request.NewFingerprint) throw RoutineDenied();
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true); var snapshot = Read(c, tx);
        var plan = HostTrustPlanning.Build(snapshot);
        if (!snapshot.Initialized || plan.Publication?.CurrentFingerprint != actualLocalFingerprint ||
            (actualLocalFingerprint != request.OldFingerprint && actualLocalFingerprint != request.NewFingerprint)) throw RoutineDenied();
        var rotation = snapshot.Rotations.SingleOrDefault(r => r.RotationId == request.RotationId);
        if (rotation is null) return new(request, RoutineRotationLiveState.Unknown);
        if (snapshot.Credentials.SingleOrDefault(c => c.Reference == rotation.OldReference)?.PublicKeyFingerprint != request.OldFingerprint ||
            snapshot.Credentials.SingleOrDefault(c => c.Reference == rotation.NewReference)?.PublicKeyFingerprint != request.NewFingerprint) throw RoutineDenied();
        using var command = Command(c, tx, "SELECT OldFingerprint,NewFingerprint FROM HostRotationProposals WHERE RotationId=$id;", ("$id", request.RotationId.ToString("D")));
        using var reader = command.ExecuteReader();
        // An upgraded legacy rotation may have no ordering journal. Its durable rotation/key
        // tuple can still answer this exact retained-state query without inventing a sequence.
        if (reader.Read() && (reader.GetString(0) != request.OldFingerprint || reader.GetString(1) != request.NewFingerprint)) throw RoutineDenied();
        var state = rotation.State switch
        {
            HostCredentialRotationState.Staging => RoutineRotationLiveState.Staging,
            HostCredentialRotationState.ReadyForCutover => RoutineRotationLiveState.ReadyForCutover,
            HostCredentialRotationState.CutOver => RoutineRotationLiveState.CutOver,
            HostCredentialRotationState.Completed => RoutineRotationLiveState.Completed,
            HostCredentialRotationState.Aborted => RoutineRotationLiveState.Aborted,
            _ => throw RoutineDenied()
        };
        var expected = state is RoutineRotationLiveState.CutOver or RoutineRotationLiveState.Completed ? request.NewFingerprint : request.OldFingerprint;
        if (actualLocalFingerprint != expected) throw RoutineDenied();
        return new(request, state);
    }
}
