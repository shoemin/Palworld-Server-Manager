using System.Security.Cryptography;
using System.Text;

namespace PalworldServerManager.Contracts;

// Versioned public framing only. Authentication/lifetime/authority are owned by Host.
public static class LocalPrincipalAuthentication
{
    public const string Domain = "PSM.LOCAL.AUTH/1";
    public static byte[] EncodeChallenge(Guid hostId, Guid principalId, ReadOnlySpan<byte> connectionBinding, ReadOnlySpan<byte> nonce)
    {
        if (hostId == Guid.Empty || principalId == Guid.Empty || connectionBinding.Length != 32 || nonce.Length != 32)
            throw new ArgumentException("Invalid local challenge identity or nonce.");
        return Encoding.ASCII.GetBytes($"{Domain}\n{hostId:D}\n{principalId:D}\n{Convert.ToBase64String(connectionBinding)}\n{Convert.ToBase64String(nonce)}");
    }

    public static bool IsValidPublicKey(string encoded)
    {
        using var key = ImportPublicKey(encoded);
        return key is not null;
    }

    public static bool Verify(string publicKey, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 64 || payload.Length is 0 or > 256) return false;
        using var key = ImportPublicKey(publicKey);
        try { return key is not null && key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation); }
        catch (CryptographicException) { return false; }
    }

    private static ECDsa? ImportPublicKey(string encoded)
    {
        if (encoded is null || encoded.Length is 0 or > 512) return null;
        ECDsa? key = null;
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (Convert.ToBase64String(bytes) != encoded) return null;
            key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
                !key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(bytes))
            { key.Dispose(); return null; }
            return key;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        { key?.Dispose(); return null; }
    }
}
