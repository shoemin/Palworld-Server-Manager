using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PalworldServerManager.Platform.Windows;

/// <summary>
/// Derives the per-service SID for the dedicated virtual account (<c>NT SERVICE\&lt;name&gt;</c>).
///
/// WHY DERIVE RATHER THAN RESOLVE BY NAME: <see cref="NTAccount.Translate"/> can only resolve
/// <c>NT SERVICE\&lt;name&gt;</c> once the service actually exists, which would force the Host data
/// directory and its ACL to be created strictly after service creation. The service SID is a pure
/// function of the service name, so deriving it removes that ordering dependency entirely - the
/// same value Windows itself reports via <c>sc showsid</c>, including for a service that has not
/// been created yet.
///
/// Format: S-1-5-80-{5 little-endian uint32s of SHA-1(UPPERCASE service name as UTF-16LE)}.
/// </summary>
public static class ServiceSecurityIdentifier
{
    public static SecurityIdentifier ForServiceName(string serviceName)
        => new(ToSddl(serviceName));

    public static string ToSddl(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var upper = serviceName.ToUpperInvariant();
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(upper));

        var builder = new StringBuilder("S-1-5-80");
        for (var i = 0; i < 5; i++)
        {
            builder.Append('-').Append(BitConverter.ToUInt32(hash, i * 4));
        }

        return builder.ToString();
    }
}
