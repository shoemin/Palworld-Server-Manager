namespace PalworldServerManager.Client.Platform.Contracts;

/// <summary>
/// Opaque local-principal key material. The ALGORITHM is deliberately not fixed here: #41
/// implements storage lifecycle only, and #42 selects the production signature algorithm.
/// AlgorithmId is persisted so a future real algorithm is distinguishable and the stored format
/// stays forward-compatible.
/// </summary>
public sealed record LocalPrincipalKeyMaterial(string AlgorithmId, byte[] PrivateKeyBlob, byte[] PublicKeyBlob);

/// <summary>
/// Generates local-principal key material. OS-neutral and algorithm-neutral BY DESIGN.
///
/// This seam is what lets #41 implement the exact SS3a store lifecycle and Windows DPAPI
/// protection WITHOUT deciding the signature algorithm that #42 owns. #41 ships no production
/// generator at all; its tests inject a deterministic fake for lifecycle coverage only, which is
/// explicitly not a production cryptographic implementation.
/// </summary>
public interface ILocalPrincipalKeyPairGenerator
{
    LocalPrincipalKeyMaterial Generate();
}

public sealed record LocalPrincipalKeyPair(string AlgorithmId, byte[] PublicKeyBlob);

/// <summary>The persisted binding: this user's LocalPrincipalId together with its private key.</summary>
public sealed record LocalPrincipalClientCredential(string LocalPrincipalId, string AlgorithmId, byte[] PrivateKeyBlob, byte[] PublicKeyBlob);

/// <summary>
/// SS3a's client-side credential store. The private key is created and held CLIENT-SIDE by that
/// OS user and never leaves it (LOCAL-002); the Host only ever stores the public verifier.
///
/// Storage is per-OS-user, so Client.Avalonia and Client.Cli running as the same user resolve the
/// same binding (CLIENT-003). No Host machine credential is reachable through this store
/// (CLIENT-002) - there is no API for one.
/// </summary>
public interface ILocalPrincipalCredentialStore
{
    Task<bool> HasCredentialAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates and persists one unbound keypair. Idempotent while an unbound key exists: calling
    /// again returns the SAME stored key rather than regenerating.
    /// </summary>
    Task<LocalPrincipalKeyPair> CreateAndStoreAsync(CancellationToken ct = default);

    Task BindPrincipalIdAsync(string localPrincipalId, CancellationToken ct = default);

    /// <summary>Returns null while no credential exists OR while it is still unbound.</summary>
    Task<LocalPrincipalClientCredential?> LoadAsync(CancellationToken ct = default);

    Task DeleteAsync(CancellationToken ct = default);
}
