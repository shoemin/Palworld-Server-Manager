using System.Security.Cryptography;
using System.Text.Json;
using PalworldServerManager.Client.Platform.Contracts;

namespace PalworldServerManager.Client.Platform.Windows;

// Only the current user's client credential. The whole versioned payload is DPAPI protected.
// No public API accepts a Host credential reference or another user's identity.
public sealed partial class WindowsLocalPrincipalCredentialStore : ILocalPrincipalCredentialStore, ILocalPrincipalCredentialCeremonyStore
{
    private readonly string _path;
    private readonly ILocalPrincipalKeyGenerator _generator;
    private readonly Action<string, byte[]> _atomicWrite;
    public WindowsLocalPrincipalCredentialStore(ILocalPrincipalKeyGenerator generator)
        : this(generator, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PalworldServerManager", "Client", "principal.bin")) { }

    // Path and write seam are injected for storage/integration tests, never selected from RPC.
    public WindowsLocalPrincipalCredentialStore(ILocalPrincipalKeyGenerator generator, string path, Action<string, byte[]>? atomicWrite = null)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _path = Path.GetFullPath(path);
        _atomicWrite = atomicWrite ?? AtomicWrite;
    }
    private sealed class Payload
    {
        public int Version { get; set; } = 1;
        public Guid? PrincipalId { get; set; }
        public byte[] PublicKey { get; set; } = [];
        public byte[] PrivateKey { get; set; } = [];
        public Guid? HostId { get; set; }
        public PendingCredential? Pending { get; set; }
        public CompletedCredential? Completed { get; set; }
    }
    private Payload? Read()
    {
        if (!File.Exists(_path)) return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
        try
        {
            var value = JsonSerializer.Deserialize<Payload>(plain) ?? throw new InvalidDataException("Invalid client credential.");
            try { ValidatePayload(value); }
            catch { Clear(value); throw; }
            return value;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private void Write(Payload value)
    {
        value.Version = 2;
        ValidatePayload(value);
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try { _atomicWrite(_path, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private async Task<FileStream> LockAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var deadline = Environment.TickCount64 + 10000;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < deadline) { await Task.Delay(25, ct); }
        }
    }
    public async Task<bool> HasCredentialAsync(CancellationToken ct = default)
    {
        using var held = await LockAsync(ct);
        var value = Read();
        try { return value?.PrincipalId is not null; }
        finally { Clear(value); }
    }
    public async Task<LocalPrincipalKeyPair> CreateAndStoreAsync(CancellationToken ct = default)
    {
        using var held = await LockAsync(ct);
        var value = Read();
        try
        {
            if (value?.Pending is not null) throw new InvalidOperationException("Complete or explicitly discard the pending ceremony first.");
            if (value?.PrivateKey.Length > 0) return Copy(value.PublicKey, value.PrivateKey);
            var pair = _generator.Generate();
            try { Write(new Payload { PublicKey = pair.PublicKey, PrivateKey = pair.PrivateKey }); return Copy(pair.PublicKey, pair.PrivateKey); }
            finally { CryptographicOperations.ZeroMemory(pair.PrivateKey); }
        }
        finally { Clear(value); }
    }
    public async Task BindPrincipalIdAsync(Guid localPrincipalId, CancellationToken ct = default)
    {
        if (localPrincipalId == Guid.Empty) throw new ArgumentException("A principal identity is required.");
        using var held = await LockAsync(ct);
        var value = Read() ?? throw new InvalidOperationException("Create the client credential first.");
        try
        {
            if (value.Pending is not null || value.Completed is not null || value.HostId is not null)
                throw new InvalidOperationException("Use exact ceremony confirmation for this credential.");
            if (value.PrincipalId is not null && value.PrincipalId != localPrincipalId)
                throw new InvalidOperationException("Delete the prior credential before rebinding another principal.");
            value.PrincipalId = localPrincipalId;
            Write(value);
        }
        finally { Clear(value); }
    }
    public async Task<LocalPrincipalClientCredential?> LoadAsync(CancellationToken ct = default)
    {
        using var held = await LockAsync(ct);
        var value = Read();
        try { return value?.PrincipalId is Guid id ? new(id, Copy(value.PublicKey, value.PrivateKey)) : null; }
        finally { Clear(value); }
    }
    public async Task DeleteAsync(CancellationToken ct = default)
    {
        using var held = await LockAsync(ct); var value = Read();
        try
        {
            if (value?.Pending is not null) throw new InvalidOperationException("Do not delete a credential with an unresolved ceremony.");
            File.Delete(_path);
        }
        finally { Clear(value); }
    }

    public static void AtomicWrite(string path, byte[] encrypted)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(encrypted); stream.Flush(true); }
            // Same-directory atomic rename; the previous good file survives failure before it.
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
