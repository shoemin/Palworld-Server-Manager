using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Platform.Windows;

// Trusted Host/offline platform consumer under its machine lease. Private bytes stay here
// and in ISecureCredentialStore; persistence receives only the public SPKI fingerprint.
public sealed class WindowsHostCredentialMaterial(ISecureCredentialStore store)
{
    private readonly ISecureCredentialStore _store=store??throw new ArgumentNullException(nameof(store));
    public async Task<string> CreateAsync(Guid hostId, string plannedReference, CancellationToken ct=default)
    {
        if(hostId==Guid.Empty) throw new ArgumentException("Host identity required.");
        var existing=await _store.RetrieveAsync(plannedReference,ct).ConfigureAwait(false);
        if(existing is not null) { CryptographicOperations.ZeroMemory(existing); throw new InvalidOperationException("Refusing to overwrite credential material."); }
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request=new CertificateRequest("CN=PalworldServerManager-"+hostId.ToString("D"),key,HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature,true));
        using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddYears(10));
        var bytes=certificate.Export(X509ContentType.Pfx);
        try { await _store.StoreAsync(plannedReference,bytes,ct).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    }
    public async Task ValidateAsync(string reference, string expectedFingerprint, CancellationToken ct=default)
    {
        var bytes=await _store.RetrieveAsync(reference,ct).ConfigureAwait(false)??throw new CryptographicException("Host credential unavailable.");
        try
        {
            using var certificate=new X509Certificate2(bytes,(string?)null,X509KeyStorageFlags.EphemeralKeySet);
            using var key=certificate.GetECDsaPrivateKey()??throw new CryptographicException("Host credential has no private key.");
            if(key.ExportParameters(false).Curve.Oid.Value!="1.2.840.10045.3.1.7" ||
                Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))!=expectedFingerprint)
                throw new CryptographicException("Host credential does not match authoritative public metadata.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public async Task EnsureEnrollmentKeyAsync(Guid hostId, bool hasEnrollmentHistory, CancellationToken ct=default)
    {
        var name=LocalEnrollmentVerifier.KeyName(hostId); var key=await _store.RetrieveAsync(name,ct).ConfigureAwait(false);
        if(key is not null)
        { try { if(key.Length!=32) throw new CryptographicException("Enrollment key is corrupt; explicit repair is required."); } finally { CryptographicOperations.ZeroMemory(key); } return; }
        if(hasEnrollmentHistory) throw new CryptographicException("Enrollment key is missing for retained tickets; it cannot be regenerated.");
        key=RandomNumberGenerator.GetBytes(32);
        try { await _store.StoreAsync(name,key,ct).ConfigureAwait(false); } finally { CryptographicOperations.ZeroMemory(key); }
    }
}
