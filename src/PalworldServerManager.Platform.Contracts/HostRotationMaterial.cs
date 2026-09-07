namespace PalworldServerManager.Platform.Contracts;

// Trusted Host consumer under its machine lease and serialized material/cleanup boundary.
// Only public evidence crosses this seam. A known fingerprint forbids regeneration if missing.
public interface IHostRotationMaterial
{
    Task<string> EnsurePreparedAsync(Guid hostId, string reservedReference, string? expectedFingerprint,
        CancellationToken ct = default);
}
