using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PalworldServerManager.Platform.Windows;

// Host-side TLS profile for later gRPC composition. Certificates are borrowed native Host
// handles; callers retain them until their connections close. No key export or new key store.
public static class WindowsPeerTls
{
    public static string PublicFingerprint(X509Certificate2 certificate)
    {
        using var key = certificate.GetECDsaPublicKey() ?? throw new AuthenticationException("Unsupported peer credential.");
        if (key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new AuthenticationException("Unsupported peer credential.");
        return Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    }
    private static bool Validate(X509Certificate? certificate, Func<string, bool> acceptsPin)
    {
        if (certificate is null) return false;
        try
        {
            using var publicCertificate = new X509Certificate2(certificate);
            var now = DateTime.UtcNow;
            return publicCertificate.NotBefore.ToUniversalTime() <= now && publicCertificate.NotAfter.ToUniversalTime() > now &&
                acceptsPin(PublicFingerprint(publicCertificate));
        }
        catch { return false; }
    }
    private static void Own(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey || !Validate(certificate, _ => true)) throw new AuthenticationException("A usable Host TLS credential is required.");
    }
    public static SslClientAuthenticationOptions ClientOptions(X509Certificate2 ownCertificate, Func<string, bool> acceptsServerPin)
    {
        ArgumentNullException.ThrowIfNull(acceptsServerPin); Own(ownCertificate);
        return new()
        {
            TargetHost = "palworld-manager-peer", // Address/DNS/Subject never replaces the stable Host's public-key pin.
            ClientCertificates = new X509CertificateCollection { ownCertificate },
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ApplicationProtocols = [SslApplicationProtocol.Http2],
            AllowRenegotiation = false, AllowTlsResume = false,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, certificate, _, _) => Validate(certificate, acceptsServerPin)
        };
    }
    public static SslServerAuthenticationOptions ServerOptions(X509Certificate2 ownCertificate, Func<string, bool> acceptsClientPin)
    {
        ArgumentNullException.ThrowIfNull(acceptsClientPin); Own(ownCertificate);
        return new()
        {
            ServerCertificate = ownCertificate, ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ApplicationProtocols = [SslApplicationProtocol.Http2],
            AllowRenegotiation = false, AllowTlsResume = false,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, certificate, _, _) => Validate(certificate, acceptsClientPin)
        };
    }
    public static Task<SslStream> AuthenticateClientAsync(Stream ownedTransport, X509Certificate2 ownCertificate,
        Func<string, bool> acceptsServerPin, CancellationToken ct = default)
        => Authenticate(ownedTransport, (ssl, token) => ssl.AuthenticateAsClientAsync(ClientOptions(ownCertificate, acceptsServerPin), token), ct);
    public static Task<SslStream> AuthenticateServerAsync(Stream ownedTransport, X509Certificate2 ownCertificate,
        Func<string, bool> acceptsClientPin, CancellationToken ct = default)
        => Authenticate(ownedTransport, (ssl, token) => ssl.AuthenticateAsServerAsync(ServerOptions(ownCertificate, acceptsClientPin), token), ct);
    private static async Task<SslStream> Authenticate(Stream ownedTransport, Func<SslStream, CancellationToken, Task> handshake, CancellationToken ct)
    {
        var ssl = new SslStream(ownedTransport, leaveInnerStreamOpen: false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await handshake(ssl, deadline.Token).ConfigureAwait(false);
            if (!ssl.IsMutuallyAuthenticated || ssl.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2)
                throw new AuthenticationException();
            ct.ThrowIfCancellationRequested(); return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw new AuthenticationException("Mutual peer TLS handshake refused.");
        }
    }
}
