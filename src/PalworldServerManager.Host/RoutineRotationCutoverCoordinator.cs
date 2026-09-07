using System.Security.Authentication;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

// One per Host. The caller holds the machine lease and has already drained/stopped all
// connections and competing mutations. This helper never resumes listeners, even on error.
// After any failure, reconcile authoritative trust before serving again; never restore Old
// merely because publication failed after the durable CutOver transaction.
internal sealed class RoutineRotationCutoverCoordinator(HostCredentialStateRepository state, RoutineRotationAcceptanceCollector collector,
    IHostRotationMaterial material, ILocalHostTrustPublisher publisher)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    internal async Task<RoutineRotationPreparation> CutOverWhileQuiescedAsync(LocalPrincipalMutationActor owner,
        RotationAcceptanceCollection collection, CancellationToken ct = default)
    {
        collector.RequireScope(collection);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var proposal = collection.Snapshot.Proposal;
            var rotation = state.InspectRoutineRotationCutover(owner, proposal);
            var fingerprint = await material.EnsurePreparedAsync(proposal.HostId, rotation.NewReference, proposal.NewFingerprint, ct).ConfigureAwait(false);
            if (fingerprint != proposal.NewFingerprint) throw new AuthenticationException("Prepared rotation material changed.");
            ct.ThrowIfCancellationRequested();
            if (rotation.State != HostCredentialRotationState.CutOver)
            {
                if (!collector.Recheck(collection).PeerAcknowledgementsReady) throw new AuthenticationException("Rotation peer acceptance is not current.");
                await publisher.PublishAsync(new(proposal.HostId, proposal.OldFingerprint, proposal.NewFingerprint, proposal.RotationId), ct).ConfigureAwait(false);
                rotation = state.CommitRoutineRotationCutover(owner, proposal, current => collector.AssessCurrent(collection, current).PeerAcknowledgementsReady, ct);
            }
            await publisher.PublishAsync(new(proposal.HostId, proposal.NewFingerprint), ct).ConfigureAwait(false);
            return rotation;
        }
        finally { gate.Release(); }
    }
}
