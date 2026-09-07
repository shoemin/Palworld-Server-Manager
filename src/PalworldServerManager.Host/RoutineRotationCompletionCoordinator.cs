using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

// Created only by a successfully stopped generation. The composition retains the machine
// lease and excludes competing writers until reconciliation and fresh generation startup.
internal sealed class RoutineRotationCompletionCoordinator(HostCredentialStateRepository state,string actualLocalFingerprint)
{
    private readonly SemaphoreSlim serial=new(1,1);
    internal async Task<RoutineRotationPreparation> CompleteWhileQuiescedAsync(LocalPrincipalMutationActor owner,Guid rotation,CancellationToken ct=default)
    {
        await serial.WaitAsync(ct).ConfigureAwait(false);
        try {return state.AuthorizeRoutineRotationRetirementWhileQuiesced(owner,rotation,actualLocalFingerprint,ct);}
        finally {serial.Release();}
    }
}
