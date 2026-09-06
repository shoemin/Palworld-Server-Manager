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

}
