using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

// One instance per Host under its machine lease. Composition must quiesce all material work
// before reconciliation/cleanup; this gate serializes this coordinator's concurrent requests.
internal sealed class RoutineRotationMaterialCoordinator(HostCredentialStateRepository state, IHostRotationMaterial material)
{
    private readonly HostCredentialStateRepository _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly IHostRotationMaterial _material = material ?? throw new ArgumentNullException(nameof(material));
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<RoutineRotationPreparation> PrepareAsync(LocalPrincipalMutationActor owner, Guid requestId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var prepared = _state.PrepareRoutineRotation(owner, requestId);
            if (prepared.State != HostCredentialRotationState.Prepared) return prepared;
            var metadata = _state.Read().Credentials.Single(c => c.Reference == prepared.NewReference);
            var fingerprint = await _material.EnsurePreparedAsync(owner.HostId, prepared.NewReference, metadata.PublicKeyFingerprint, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return _state.RecordRoutineRotationMaterial(owner, prepared.RotationId, fingerprint);
        }
        finally { _gate.Release(); }
    }
}
