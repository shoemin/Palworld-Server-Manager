using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PalworldServerManager.Client.Platform.Contracts;

namespace PalworldServerManager.Client.Platform.Windows;

/// <summary>
/// Windows <see cref="ILocalPrincipalCredentialStore"/> using DPAPI at CurrentUser scope (SS3a,
/// SS7, LOCAL-002, SEC-001).
///
/// The private key never touches Host, Host.Persistence, or the Host secure store. Storage is
/// per-OS-user, so Client.Avalonia and Client.Cli running as the same user resolve the same
/// binding (CLIENT-003).
///
/// CRYPTO BOUNDARY: this class implements storage LIFECYCLE only. Key generation is injected via
/// ILocalPrincipalKeyPairGenerator, so #42 keeps the production signature-algorithm decision. #41
/// deliberately ships no production generator.
///
/// CROSS-PROCESS SAFETY: Client.Avalonia and Client.Cli may run simultaneously as the same
/// Windows user and intentionally share ONE credential (CLIENT-003). Every read-modify-write
/// operation therefore acquires an exclusive file lock (localprincipal.v1.lock) for its whole
/// duration - a plain FileStream opened with FileShare.None, not a Mutex, so there is no thread
/// affinity and a crashed process releases it automatically when the OS reclaims its handles.
/// </summary>
public sealed class WindowsLocalPrincipalCredentialStore : ILocalPrincipalCredentialStore
{
    private const int FormatVersion = 1;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    // Per-purpose DPAPI entropy: a blob protected for this purpose cannot be unprotected under a
    // different purpose string, even by the same user.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PalworldServerManager.LocalPrincipal.v1");

    private readonly string _filePath;
    private readonly string _lockPath;
    private readonly ILocalPrincipalKeyPairGenerator _generator;

    public WindowsLocalPrincipalCredentialStore(ILocalPrincipalKeyPairGenerator generator, string? storageDirectory = null)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));

        var directory = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PalworldServerManager");

        _filePath = Path.Combine(directory, "localprincipal.v1.bin");
        _lockPath = _filePath + ".lock";
    }

    public string FilePath => _filePath;

    public Task<bool> HasCredentialAsync(CancellationToken ct = default)
    {
        var record = TryRead();
        // False while unbound: a key with no LocalPrincipalId is not yet a usable credential.
        return Task.FromResult(record is { LocalPrincipalId.Length: > 0 });
    }

    public Task<LocalPrincipalKeyPair> CreateAndStoreAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = AcquireLock(ct);

        var existing = TryRead();
        if (existing is not null)
        {
            // Idempotent while unbound - and equally, never silently replaces a bound credential.
            // Serialized by the lock above: two racing callers can never both observe "no file"
            // and each generate their own key - only the first to acquire the lock generates one,
            // and every later caller (in this process or another) sees it via this branch.
            return Task.FromResult(new LocalPrincipalKeyPair(existing.AlgorithmId, existing.PublicKeyBlob));
        }

        var material = _generator.Generate();
        Write(new StoredCredential(FormatVersion, material.AlgorithmId, material.PrivateKeyBlob, material.PublicKeyBlob, LocalPrincipalId: null));
        return Task.FromResult(new LocalPrincipalKeyPair(material.AlgorithmId, material.PublicKeyBlob));
    }

    public Task BindPrincipalIdAsync(string localPrincipalId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPrincipalId);
        ct.ThrowIfCancellationRequested();
        using var _ = AcquireLock(ct);

        var existing = TryRead()
            ?? throw new InvalidOperationException("No local principal key exists to bind; call CreateAndStoreAsync first.");

        if (existing.LocalPrincipalId is { Length: > 0 } boundId)
        {
            if (string.Equals(boundId, localPrincipalId, StringComparison.Ordinal))
            {
                // Idempotent success: rebinding to the SAME principal is a no-op, not an error.
                return Task.CompletedTask;
            }

            // Never silently rebind one private key to a different semantic principal.
            throw new InvalidOperationException(
                $"The stored local-principal credential is already bound to '{boundId}' and cannot be rebound to '{localPrincipalId}'.");
        }

        Write(existing with { LocalPrincipalId = localPrincipalId });
        return Task.CompletedTask;
    }

    public Task<LocalPrincipalClientCredential?> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var record = TryRead();
        if (record?.LocalPrincipalId is not { Length: > 0 } id)
        {
            // Null while absent OR while still unbound.
            return Task.FromResult<LocalPrincipalClientCredential?>(null);
        }

        return Task.FromResult<LocalPrincipalClientCredential?>(
            new LocalPrincipalClientCredential(id, record.AlgorithmId, record.PrivateKeyBlob, record.PublicKeyBlob));
    }

    public Task DeleteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = AcquireLock(ct);

        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Acquires the exclusive cross-process file lock guarding read-modify-write access to the
    /// credential. A plain locked FileStream, not a Mutex: no thread affinity, and a crashed
    /// holder's handle is released by the OS automatically rather than requiring abandonment
    /// handling.
    ///
    /// Cancellation is checked before every attempt AND observed promptly while waiting between
    /// retries (via the token's own WaitHandle rather than an uninterruptible Thread.Sleep), so a
    /// caller that cancels does not have to wait out the full contention window.
    /// </summary>
    private FileStream AcquireLock(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_lockPath)!;
        Directory.CreateDirectory(directory);

        var deadline = DateTime.UtcNow.Add(LockTimeout);
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                // Blocks for at most 25ms, but returns IMMEDIATELY once ct is canceled.
                ct.WaitHandle.WaitOne(25);
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    private StoredCredential? TryRead()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        var protectedBytes = File.ReadAllBytes(_filePath);
        var plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        var record = JsonSerializer.Deserialize<StoredCredential>(plaintext)
            ?? throw new InvalidOperationException("The stored local-principal credential could not be deserialized.");

        if (record.Version != FormatVersion)
        {
            throw new InvalidOperationException(
                $"Stored local-principal credential format version {record.Version} is not supported by this build (expected {FormatVersion}).");
        }

        return record;
    }

    /// <summary>
    /// Atomic write: serialize, DPAPI-protect, write to a temp file, then replace. An interrupted
    /// write can therefore never corrupt an existing good credential - the old file is replaced
    /// only once the new one is fully written.
    /// </summary>
    private void Write(StoredCredential record)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(record);
        var protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

        var temp = _filePath + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, _filePath, overwrite: true);
    }

    private sealed record StoredCredential(
        int Version,
        string AlgorithmId,
        byte[] PrivateKeyBlob,
        byte[] PublicKeyBlob,
        string? LocalPrincipalId);
}
