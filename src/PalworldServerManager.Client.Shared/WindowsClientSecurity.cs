using System.Security.Principal;
using PalworldServerManager.Client.Platform.Windows;

namespace PalworldServerManager.Client.Security;

// The ordinary executables' single Windows composition, compiled from the same source.
// Instantiation is lazy; launching an unconfigured UI need not access a missing service.
public static class WindowsClientSecurity
{
    public static LocalSecurityClient Create()
    {
        var product = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PalworldServerManager");
        var service = (SecurityIdentifier)new NTAccount("NT SERVICE", WindowsHostActivation.ProductServiceName).Translate(typeof(SecurityIdentifier));
        var trust = new WindowsLocalHostTrustReader(Path.Combine(product, "PublicTrust"), service);
        var cryptography = new WindowsLocalPrincipalCryptography();
        var credentials = new WindowsLocalPrincipalCredentialStore(cryptography);
        return new(trust, new WindowsLocalHostHttpTransportFactory(trust, "PalworldServerManager.Host"), new WindowsHostActivation(),
            credentials, credentials, cryptography,
            (host, ticket) => new WindowsOwnerHandoffReader(Path.Combine(product, "OwnerHandoffs"), host, ticket));
    }
}
