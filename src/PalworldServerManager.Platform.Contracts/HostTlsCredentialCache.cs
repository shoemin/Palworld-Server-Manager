using System.Security.Cryptography.X509Certificates;

namespace PalworldServerManager.Platform.Contracts;

/// <summary>Derived TLS-use cache only. Trusted Host/offline composition holds the machine lease.
/// Load always requires the authoritative secure-store credential; a cache is never a recovery
/// source. Dispose returned certificates after all TLS connections using them are stopped.
/// Reconcile only after those connections stop, using the complete authoritative retained set.</summary>
public interface IHostTlsCredentialCache
{
    Task<X509Certificate2> LoadAsync(string credentialReference, CancellationToken ct = default);
    Task ReconcileAsync(IReadOnlyCollection<string> retainedCredentialReferences, CancellationToken ct = default);
}
