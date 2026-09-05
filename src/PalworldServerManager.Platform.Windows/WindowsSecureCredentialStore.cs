using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Platform.Windows;

/// <summary>Machine DPAPI plus dedicated service/elevated Administrators/SYSTEM NTFS boundary.
/// Never instantiate with RPC-supplied paths/SIDs. Host/Host.Cli composition owns exclusivity.
/// Does not protect against privileged machine owners, volume snapshots or memory inspection.</summary>
public sealed class WindowsSecureCredentialStore : ISecureCredentialStore
{
    public const int MaximumSecretBytes = 1024 * 1024;
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate;
    private static readonly byte[] Header = "PSM1"u8.ToArray();
    private readonly DirectoryInfo _hostRoot;
    private readonly DirectoryInfo _store;
    private readonly SecurityIdentifier _service;
    private readonly SecurityIdentifier[] _allowed;

    public WindowsSecureCredentialStore() : this(new WindowsHostPlatform().GetHostDataRoot(),
        (SecurityIdentifier)new NTAccount("NT SERVICE", WindowsHostPlatform.ProductServiceName).Translate(typeof(SecurityIdentifier))) { }

    // Explicit trusted composition parameters also permit isolated real-service integration.
    public WindowsSecureCredentialStore(string hostDataRoot, SecurityIdentifier serviceSid)
    {
        if (!Path.IsPathFullyQualified(hostDataRoot) || hostDataRoot.StartsWith(@"\\"))
            throw new ArgumentException("An absolute local Host root is required.");
        _hostRoot = new DirectoryInfo(Path.GetFullPath(hostDataRoot));
        _gate = Gates.GetOrAdd(_hostRoot.FullName, _ => new object());
        _store = new DirectoryInfo(Path.Combine(_hostRoot.FullName, "credentials"));
        _service = serviceSid ?? throw new ArgumentNullException(nameof(serviceSid));
        _allowed = [_service, new(WellKnownSidType.BuiltinAdministratorsSid, null), new(WellKnownSidType.LocalSystemSid, null)];
    }

    public Task StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct = default)
    { lock (_gate) return StoreCore(key, secret, ct); }
    private Task StoreCore(string key, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        var hash = KeyHash(key); ct.ThrowIfCancellationRequested();
        if (secret.Length > MaximumSecretBytes) throw new ArgumentException("Credential exceeds the store size limit.");
        Prepare(); RetireTemporaryFiles(hash); var destination = Blob(hash); ValidateBlobIfPresent(destination);
        var plaintext = secret.ToArray(); byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plaintext, hash, DataProtectionScope.LocalMachine); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        var temporary = Path.Combine(_store.FullName, Convert.ToHexString(hash) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try
        {
            var security = new FileSecurity(); SetPolicy(security);
            using (var stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.Write,
                FileShare.None, 4096, FileOptions.WriteThrough, security))
            {
                created = true; stream.Write(Header); stream.Write(encrypted); stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            Prepare(); ValidateBlobIfPresent(destination);
            File.Move(temporary, destination, true); // same-directory atomic name replacement
        }
        finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
        return Task.CompletedTask;
    }

    public Task<byte[]?> RetrieveAsync(string key, CancellationToken ct = default)
    { lock (_gate) return RetrieveCore(key, ct); }
    private Task<byte[]?> RetrieveCore(string key, CancellationToken ct)
    {
        var hash = KeyHash(key); ct.ThrowIfCancellationRequested(); Prepare(); var path = Blob(hash);
        if (!ValidateBlobIfPresent(path)) return Task.FromResult<byte[]?>(null);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < Header.Length || stream.Length > MaximumSecretBytes + 65536)
            throw new CryptographicException("Invalid encrypted credential envelope.");
        var envelope = new byte[(int)stream.Length]; stream.ReadExactly(envelope);
        if (!envelope.AsSpan(0, Header.Length).SequenceEqual(Header))
            throw new CryptographicException("Unsupported encrypted credential format.");
        var result = ProtectedData.Unprotect(envelope.AsSpan(Header.Length).ToArray(), hash, DataProtectionScope.LocalMachine);
        if (result.Length > MaximumSecretBytes) { CryptographicOperations.ZeroMemory(result); throw new CryptographicException("Invalid credential size."); }
        if (ct.IsCancellationRequested) { CryptographicOperations.ZeroMemory(result); ct.ThrowIfCancellationRequested(); }
        return Task.FromResult<byte[]?>(result);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    { lock (_gate) return DeleteCore(key, ct); }
    private Task DeleteCore(string key, CancellationToken ct)
    {
        var hash = KeyHash(key); ct.ThrowIfCancellationRequested(); Prepare(); var path = Blob(hash);
        RetireTemporaryFiles(hash);
        if (ValidateBlobIfPresent(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private void RetireTemporaryFiles(byte[] hash)
    {
        // Per-root in-process serialization plus the caller's machine lease exclude live writers.
        // A retry/delete retires encrypted interrupted writes for this exact key as well.
        foreach (var path in Directory.EnumerateFiles(_store.FullName, Convert.ToHexString(hash) + ".*.tmp"))
            if (ValidateBlobIfPresent(path)) File.Delete(path);
    }
    private static byte[] KeyHash(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 128 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("Credential key must be 1–128 ASCII identifier characters.");
        return SHA256.HashData(Encoding.UTF8.GetBytes("PalworldServerManager.Host.Credential.v1:" + key));
    }
    private string Blob(byte[] hash) => Path.Combine(_store.FullName, Convert.ToHexString(hash) + ".bin");
    private void Prepare()
    {
        // A protected root must already be provisioned by #41. Never repair/adopt unsafe state.
        for (DirectoryInfo? ancestor = _hostRoot; ancestor is not null; ancestor = ancestor.Parent)
        {
            ancestor.Refresh();
            if (!ancestor.Exists || (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Host credential path is missing or traverses a reparse point.");
        }
        ValidateAcl(_hostRoot.GetAccessControl());
        _store.Refresh();
        if (!_store.Exists)
        {
            var security = new DirectorySecurity(); SetPolicy(security);
            _store.Create(security);
        }
        _store.Refresh();
        if ((_store.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Credential store cannot be a reparse point.");
        ValidateAcl(_store.GetAccessControl());
    }
    private bool ValidateBlobIfPresent(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("Credential blob must be a regular file.");
        ValidateAcl(new FileInfo(path).GetAccessControl());
        return true;
    }
    private void SetPolicy(FileSystemSecurity security)
    {
        security.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        security.SetOwner(identity.User == _service || identity.User == _allowed[2] ? identity.User : _allowed[1]);
        foreach (var sid in _allowed.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                security is DirectorySecurity ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
                PropagationFlags.None, AccessControlType.Allow));
    }
    private void ValidateAcl(FileSystemSecurity security)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !_allowed.Contains(owner))
            throw new UnauthorizedAccessException("Credential storage owner is outside the approved boundary.");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (!security.AreAccessRulesProtected || rules.Any(r => r.AccessControlType != AccessControlType.Allow || !_allowed.Contains(r.IdentityReference)))
            throw new UnauthorizedAccessException("Credential storage permissions require privileged repair.");
        foreach (var sid in _allowed)
            if (!rules.Any(r => r.IdentityReference == sid && (r.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (r.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl))
                throw new UnauthorizedAccessException("Credential storage lacks required service/recovery access.");
    }
}
