using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Platform.Windows;

/// <summary>Approved Windows Schannel cache. Never call during SCM OnStart: initialize on the
/// Host worker after SCM start returns, before accepting any connection. Caller owns machine lease.</summary>
public sealed class WindowsHostTlsCredentialCache : IHostTlsCredentialCache
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);
    private readonly string _prefix;
    private readonly object _gate;
    private readonly ISecureCredentialStore _store;
    private readonly SecurityIdentifier[] _allowed;
    public WindowsHostTlsCredentialCache(Guid hostId, SecurityIdentifier serviceSid, ISecureCredentialStore store)
    {
        if (hostId == Guid.Empty) throw new ArgumentException("Host identity is required.");
        _prefix = "PalworldServerManager.HostTls.v1." + hostId.ToString("N") + ".";
        _gate = Gates.GetOrAdd(_prefix, _ => new object());
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _allowed = [serviceSid ?? throw new ArgumentNullException(nameof(serviceSid)), new(WellKnownSidType.BuiltinAdministratorsSid, null), new(WellKnownSidType.LocalSystemSid, null)];
    }
    public async Task<X509Certificate2> LoadAsync(string credentialReference, CancellationToken ct = default)
    {
        var name = Name(credentialReference); ct.ThrowIfCancellationRequested();
        var pfx = await _store.RetrieveAsync(credentialReference, ct)
            ?? throw new CryptographicException("Authoritative Host credential is unavailable.");
        try
        {
            using var authority = new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            using var signingKey = authority.GetECDsaPrivateKey() ?? throw new CryptographicException("Host credential must contain an ECDSA private key.");
            if (signingKey.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new CryptographicException("Unsupported Host credential curve.");
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested(); using var provider = NativeTlsKeys.Provider();
                using (var existing = NativeTlsKeys.Open(provider, name))
                {
                    if (existing is null)
                    {
                        if (signingKey is ECDsaCng transient)
                            transient.Key.SetProperty(new CngProperty("Export Policy", BitConverter.GetBytes(3), CngPropertyOptions.None));
                        var privateKey = signingKey.ExportPkcs8PrivateKey();
                        try { NativeTlsKeys.Import(provider, name, privateKey, NewSecurity()); }
                        finally { CryptographicOperations.ZeroMemory(privateKey); }
                    }
                }
                using var handle = NativeTlsKeys.Open(provider, name) ?? throw new CryptographicException("Native cache creation did not persist.");
                Validate(handle);
                using var key = CngKey.Open(handle, CngKeyHandleOpenOptions.None);
                using var cached = new ECDsaCng(key);
                if (!CryptographicOperations.FixedTimeEquals(cached.ExportSubjectPublicKeyInfo(), signingKey.ExportSubjectPublicKeyInfo()))
                    throw new CryptographicException("Native cache does not match the authoritative credential.");
                using var publicCertificate = new X509Certificate2(authority.RawData);
                ct.ThrowIfCancellationRequested();
                return publicCertificate.CopyWithPrivateKey(cached);
            }
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
    public Task ReconcileAsync(IReadOnlyCollection<string> retainedCredentialReferences, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(retainedCredentialReferences);
        var retained = retainedCredentialReferences.Select(Name).ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested(); using var provider = NativeTlsKeys.Provider();
            foreach (var name in NativeTlsKeys.Names(provider, _prefix))
            {
                ct.ThrowIfCancellationRequested();
                using var key = NativeTlsKeys.Open(provider, name);
                if (key is null) continue;
                Validate(key); // Unsafe state is not adopted, repaired or silently discarded.
                if (!retained.Contains(name)) NativeTlsKeys.Delete(key);
            }
        }
        return Task.CompletedTask;
    }
    private string Name(string reference)
    {
        if (string.IsNullOrEmpty(reference) || reference.Length > 128 || reference.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new ArgumentException("Invalid opaque credential reference.");
        return _prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference)));
    }
    private byte[] NewSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User == _allowed[0] || identity.User == _allowed[2] ? identity.User! : _allowed[1];
        var sddl = "O:" + owner.Value + "D:P" + string.Concat(_allowed.Distinct().Select(sid => "(A;;GA;;;" + sid.Value + ")"));
        var descriptor = new RawSecurityDescriptor(sddl); var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0); return bytes;
    }
    private void Validate(SafeNCryptKeyHandle handle)
    {
        ValidateDescriptor(new RawSecurityDescriptor(NativeTlsKeys.Security(handle), 0));
        using var key = CngKey.Open(handle, CngKeyHandleOpenOptions.None);
        if (key.ExportPolicy != CngExportPolicies.None || !key.IsMachineKey)
            throw new UnauthorizedAccessException("Native cache protection policy is unsafe.");
        var uniqueName = key.UniqueName;
        if (string.IsNullOrEmpty(uniqueName) || uniqueName != Path.GetFileName(uniqueName)) throw new CryptographicException("Invalid native key filename.");
        var directory = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Crypto", "Keys"));
        for (DirectoryInfo? ancestor = directory; ancestor is not null; ancestor = ancestor.Parent)
            if (!ancestor.Exists || (ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Native key path is unsafe.");
        var file = new FileInfo(Path.Combine(directory.FullName, uniqueName));
        if (!file.Exists || (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new IOException("Native key file is unsafe.");
        ValidateDescriptor(new RawSecurityDescriptor(file.GetAccessControl().GetSecurityDescriptorBinaryForm(), 0));
    }
    private void ValidateDescriptor(RawSecurityDescriptor descriptor)
    {
        if (descriptor.Owner is null || !_allowed.Contains(descriptor.Owner) || descriptor.DiscretionaryAcl is null ||
            (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
            throw new UnauthorizedAccessException("Native key ownership/protection is outside the approved boundary.");
        var grants = new HashSet<SecurityIdentifier>();
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            if (ace is not CommonAce rule || rule.AceQualifier != AceQualifier.AccessAllowed || !_allowed.Contains(rule.SecurityIdentifier) ||
                (rule.AceFlags & (AceFlags.InheritOnly | AceFlags.Inherited)) != 0)
                throw new UnauthorizedAccessException("Native key permissions require privileged repair.");
            if ((rule.AccessMask & 0x10000000) != 0 || (rule.AccessMask & 0x1f01ff) == 0x1f01ff) grants.Add(rule.SecurityIdentifier);
        }
        if (_allowed.Any(sid => !grants.Contains(sid))) throw new UnauthorizedAccessException("Native key lacks required service/recovery access.");
    }
}
