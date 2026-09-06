using System.Security.Cryptography;
using System.Text;

namespace PalworldServerManager.Client.Platform.Contracts;

// Shared ordinary-client primitive; BCL only, no Host-side dependency or storage access.
// Returned key buffers belong to the caller and must be cleared after use.
public sealed class P256LocalPrincipalCryptography : ILocalPrincipalKeyGenerator
{
    public LocalPrincipalKeyPair Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new(key.ExportSubjectPublicKeyInfo(), key.ExportPkcs8PrivateKey());
    }

    public static byte[] Sign(LocalPrincipalClientCredential credential, Guid expectedHostId, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (expectedHostId == Guid.Empty || credential.LocalPrincipalId == Guid.Empty || payload.Length is 0 or > 256)
            throw new CryptographicException("Invalid local challenge.");
        // This client-side interpretation is independently checked against Contracts' encoder
        // in cross-boundary tests, preserving the accepted project graph.
        foreach (var value in payload)
            if (value > 127) throw new CryptographicException("Invalid local challenge encoding.");
        var text = Encoding.ASCII.GetString(payload);
        var fields = text.Split('\n');
        static bool Nonce(string value)
        {
            try { var bytes = Convert.FromBase64String(value); return bytes.Length == 32 && Convert.ToBase64String(bytes) == value; }
            catch (FormatException) { return false; }
        }
        if (fields.Length != 5 || fields[0] != "PSM.LOCAL.AUTH/1" || fields[1] != expectedHostId.ToString("D") ||
            fields[2] != credential.LocalPrincipalId.ToString("D") || !Nonce(fields[3]) || !Nonce(fields[4]))
            throw new CryptographicException("The challenge does not match this Host and local principal.");
        if (credential.KeyPair.PrivateKey.Length is 0 or > 512) throw new CryptographicException("Invalid local private key.");
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(credential.KeyPair.PrivateKey, out var read);
        if (read != credential.KeyPair.PrivateKey.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
            !key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(credential.KeyPair.PublicKey))
            throw new CryptographicException("Invalid local credential keypair.");
        return key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
