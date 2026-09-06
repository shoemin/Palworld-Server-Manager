namespace PalworldServerManager.Core.Security;

public enum MachineCredentialRecoveryReason { CredentialLoss = 1, SuspectedCompromise = 2 }
public enum HostCredentialRotationState { Prepared, Staging, ReadyForCutover, CutOver, Completed, Aborted }
public sealed record HostCredentialMetadata(string Reference, string? PublicKeyFingerprint, bool Retired);
public sealed record HostRotationMetadata(Guid RotationId, string? OldReference, string? NewReference, HostCredentialRotationState State);
public sealed record HostCredentialSnapshot(Guid HostId, bool Initialized, string? CurrentReference,
    IReadOnlyList<HostCredentialMetadata> Credentials, IReadOnlyList<HostRotationMetadata> Rotations);
public sealed record HostTrustProjection(Guid HostId, string CurrentFingerprint, string? PendingFingerprint, Guid? PendingRotationId);
public sealed record HostTrustPlan(HostTrustProjection? Publication, IReadOnlyCollection<string> Retained, IReadOnlyCollection<string> Retire);
// Only an explicitly selected offline fresh-credential recovery may resolve this condition.
// Unknown references, retired authoritative material and malformed identity remain distinct failures.
public sealed class HostTrustMetadataUnavailableException() : IOException("Authoritative credential public metadata is unavailable.");

public static class HostTrustPlanning
{
    public static bool Fingerprint(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    public static HostTrustPlan Build(HostCredentialSnapshot state)
    {
        if (state.HostId == Guid.Empty) throw new InvalidDataException("Missing authoritative Host identity.");
        var references = state.Credentials.ToDictionary(x => x.Reference, StringComparer.Ordinal);
        var retained = new HashSet<string>(StringComparer.Ordinal);
        string Require(string? reference)
        {
            if (reference is null || !references.TryGetValue(reference, out var row) || row.Retired)
                throw new InvalidDataException("Authoritative credential reference is invalid.");
            if (!Fingerprint(row.PublicKeyFingerprint)) throw new HostTrustMetadataUnavailableException();
            retained.Add(reference); return row.PublicKeyFingerprint!;
        }
        var active = state.Rotations.Where(r => r.State is not HostCredentialRotationState.Completed and not HostCredentialRotationState.Aborted).ToArray();
        if (state.Rotations.Any(r => !Enum.IsDefined(r.State)) || active.Length > 1) throw new InvalidDataException("Ambiguous credential rotation state.");
        HostTrustProjection? publication = null;
        if (state.CurrentReference is { } current)
        {
            var fingerprint = Require(current); string? pending = null; Guid? rotationId = null;
            if (active.SingleOrDefault() is { } rotation)
            {
                if (rotation.RotationId == Guid.Empty || rotation.OldReference == rotation.NewReference) throw new InvalidDataException("Invalid rotation identity.");
                Require(rotation.OldReference); var next = Require(rotation.NewReference);
                if (rotation.State == HostCredentialRotationState.CutOver)
                { if (current != rotation.NewReference) throw new InvalidDataException("Cutover disagrees with current credential."); }
                else
                {
                    if (current != rotation.OldReference) throw new InvalidDataException("Staged rotation disagrees with current credential.");
                    if (rotation.State is HostCredentialRotationState.Staging or HostCredentialRotationState.ReadyForCutover)
                    { pending = next; rotationId = rotation.RotationId; }
                }
            }
            publication = new(state.HostId, fingerprint, pending, rotationId);
        }
        else if (state.Initialized || active.Length != 0) throw new InvalidDataException("Initialized or rotating Host lacks a current credential.");
        return new(publication, retained.ToArray(), references.Keys.Where(r => !retained.Contains(r)).ToArray());
    }
}

// Pure domain orchestration: no private credential bytes, SQL or OS implementation.
// Caller retains its machine lease and quiesces all connections/mutations. This is not an
// online rotation engine. A failed step is never reported complete; the next pass is idempotent.
public sealed class HostTrustReconciler(Func<HostCredentialSnapshot> read,
    Func<HostTrustProjection, CancellationToken, Task> publish,
    Func<IReadOnlyCollection<string>, CancellationToken, Task> reconcileNative,
    Func<string, CancellationToken, Task> retireSecret, Action<string> recordRetired)
{
    public async Task<HostTrustPlan> ReconcileAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var plan = HostTrustPlanning.Build(read());
        if (plan.Publication is { } publication) await publish(publication, ct).ConfigureAwait(false);
        await reconcileNative(plan.Retained, ct).ConfigureAwait(false);
        foreach (var reference in plan.Retire)
        {
            await retireSecret(reference, ct).ConfigureAwait(false);
            recordRetired(reference); // only after actual deletion; missing is idempotent success
        }
        return plan;
    }
}
