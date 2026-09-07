using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace PalworldServerManager.Platform.Windows;

public static class WindowsPeerEndpoint
{
    public static IPAddress ReadSourceAddress(ConnectionContext connection)
        => (connection.RemoteEndPoint as IPEndPoint)?.Address ?? throw new AuthenticationException("Peer source unavailable.");
    public static void Configure(IWebHostBuilder builder, IPEndPoint endpoint, X509Certificate2 certificate,
        Func<string, bool> acceptsPin, Func<ConnectionDelegate, ConnectionDelegate> applicationMiddleware,
        Func<ConnectionDelegate, ConnectionDelegate>? transportMiddleware = null)
    {
        var profile = WindowsPeerTls.ServerOptions(certificate, acceptsPin);
        builder.ConfigureKestrel(options =>
        {
            options.Limits.MaxConcurrentConnections = 128;
            options.Limits.Http2.MaxStreamsPerConnection = 16;
            options.Listen(endpoint, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                if (transportMiddleware is not null) listen.Use(transportMiddleware);
                listen.UseHttps(certificate, tls =>
                {
                    tls.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    // Kestrel owns the SslStream constructor callback; supplying a second
                    // callback in OnAuthenticate is rejected by SslStream.
                    tls.ClientCertificateValidation = (peer, chain, errors) => profile.RemoteCertificateValidationCallback!(peer, peer, chain, errors);
                    tls.HandshakeTimeout = TimeSpan.FromSeconds(10);
                    tls.OnAuthenticate = (_, ssl) =>
                    {
                        ssl.EnabledSslProtocols = profile.EnabledSslProtocols;
                        ssl.ApplicationProtocols = profile.ApplicationProtocols;
                        ssl.ClientCertificateRequired = true;
                        ssl.AllowRenegotiation = false; ssl.AllowTlsResume = false;
                        ssl.CertificateRevocationCheckMode = profile.CertificateRevocationCheckMode;
                    };
                });
                listen.Use(applicationMiddleware);
            });
        });
    }
    // Called after Kestrel's required-certificate TLS middleware has completed.
    public static string ReadRemoteFingerprint(ConnectionContext connection)
    {
        if (connection.Features.Get<ITlsHandshakeFeature>()?.Protocol is not (SslProtocols.Tls12 or SslProtocols.Tls13) ||
            connection.Features.Get<ITlsApplicationProtocolFeature>() is not { } alpn ||
            !alpn.ApplicationProtocol.Span.SequenceEqual(SslApplicationProtocol.Http2.Protocol.Span)) throw new AuthenticationException();
        var certificate = connection.Features.Get<ITlsConnectionFeature>()?.ClientCertificate ?? throw new AuthenticationException();
        return WindowsPeerTls.PublicFingerprint(certificate);
    }
}
