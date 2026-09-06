using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Platform.Windows;

// The composition root selects this Windows profile. The certificate is borrowed until
// all transports have been disposed; each transport owns its handler and TLS connections.
public sealed class WindowsPeerHttpTransportFactory(X509Certificate2 certificate) : IPeerHttpTransportFactory
{
    public IPeerHttpTransport Create(Func<string, bool> acceptsServerPin) => new Transport(certificate, acceptsServerPin);
    private sealed class Transport : IPeerHttpTransport
    {
        private PeerTlsConnectionIdentity? identity;
        public HttpMessageHandler Handler { get; }
        public PeerTlsConnectionIdentity Identity => Volatile.Read(ref identity) ?? throw new AuthenticationException("Peer TLS proof unavailable.");
        internal Transport(X509Certificate2 certificate, Func<string, bool> acceptsServerPin)
        {
            var local = WindowsPeerTls.PublicFingerprint(certificate);
            Handler = new SocketsHttpHandler
            {
                UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10),
                SslOptions = WindowsPeerTls.ClientOptions(certificate, acceptsServerPin),
                PlaintextStreamFilter = (context, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (context.PlaintextStream is not SslStream ssl || !ssl.IsMutuallyAuthenticated ||
                        ssl.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2 || ssl.LocalCertificate is null || ssl.RemoteCertificate is null)
                        throw new AuthenticationException("Peer TLS proof refused.");
                    using var own = new X509Certificate2(ssl.LocalCertificate); using var remote = new X509Certificate2(ssl.RemoteCertificate);
                    var evidence = new PeerTlsConnectionIdentity(local, WindowsPeerTls.PublicFingerprint(remote));
                    if (WindowsPeerTls.PublicFingerprint(own) != local || Interlocked.CompareExchange(ref identity, evidence, null) is not null)
                        throw new AuthenticationException("Retry with a fresh peer connection.");
                    return ValueTask.FromResult(context.PlaintextStream);
                }
            };
        }
        public void Dispose() => Handler.Dispose();
    }
}
