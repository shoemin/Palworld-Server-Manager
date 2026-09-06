using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.Host;

// Host-owned outbound boundary. Ordinary clients never construct this or obtain its key.
// Each bounded attempt gets a new channel/negotiation; callers retry from durable trust.
internal sealed class PeerActivationRpcClient(PeerSecurityRpcRuntime runtime, X509Certificate2 certificate)
{
    internal async Task<PeerActivationDisposition> FinalizeAsync(Guid peer, Uri address, CancellationToken ct = default)
    {
        if (peer == Guid.Empty || peer == runtime.HostId || !address.IsAbsoluteUri || address.Scheme != "https" ||
            address.UserInfo.Length != 0 || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException("A reachable peer HTTPS address is required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var local = WindowsPeerTls.PublicFingerprint(certificate); string? actualPeer = null;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = WindowsPeerTls.ClientOptions(certificate, pin =>
            { runtime.Authentication.Authenticate(peer, pin, PeerTrafficPurpose.PairingFinalization); return true; }),
            PlaintextStreamFilter = (context, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (context.PlaintextStream is not SslStream ssl || !ssl.IsMutuallyAuthenticated ||
                    ssl.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2 || ssl.LocalCertificate is null || ssl.RemoteCertificate is null)
                    throw new AuthenticationException("Peer TLS proof refused.");
                using var own = new X509Certificate2(ssl.LocalCertificate); using var remote = new X509Certificate2(ssl.RemoteCertificate);
                if (WindowsPeerTls.PublicFingerprint(own) != local ||
                    Interlocked.CompareExchange(ref actualPeer, WindowsPeerTls.PublicFingerprint(remote), null) is not null)
                    throw new AuthenticationException("Retry with a fresh peer connection.");
                return ValueTask.FromResult(context.PlaintextStream);
            }
        };
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler, HttpVersion = HttpVersion.Version20, HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            MaxReceiveMessageSize = PeerSecurityRpcService.MaximumMessageBytes, MaxSendMessageSize = PeerSecurityRpcService.MaximumMessageBytes
        });
        var client = new PeerSecurityProtocol.PeerSecurityProtocolClient(channel);
        var hello = PeerSecurityRpcRuntime.Hello(runtime.HostId);
        var reply = await client.NegotiateAsync(hello, cancellationToken: deadline.Token).ResponseAsync.ConfigureAwait(false);
        if (reply.Host is null || PeerSecurityRpcService.Id(reply.Host.HostId) != peer || actualPeer is null) throw new AuthenticationException("Peer identity refused.");
        NegotiatedProtocol.Negotiate(hello.Handshake, reply.Handshake).Require(FeatureCapability.PeerTrustActivation);
        var ack = runtime.Repository.PrepareActivationAcknowledgement(peer, actualPeer, local);
        var activated = await client.ActivateAsync(PeerSecurityRpcService.Wire(ack), cancellationToken: deadline.Token).ResponseAsync.ConfigureAwait(false);
        if (activated.Acknowledgement is null || activated.Result is not (PeerActivationResult.Activated or PeerActivationResult.AlreadyActive))
            throw new AuthenticationException("Peer activation proof refused.");
        deadline.Token.ThrowIfCancellationRequested();
        return runtime.Repository.AcceptActivationAcknowledgement(peer, actualPeer, local,
            PeerSecurityRpcService.Durable(activated.Acknowledgement), runtime.Hook);
    }
}
