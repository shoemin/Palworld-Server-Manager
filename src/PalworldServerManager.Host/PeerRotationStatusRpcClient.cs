using System.Net;
using System.Security.Authentication;
using Grpc.Net.Client;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

internal enum PeerRotationStatusExchange { Unchanged = 1, Renewed = 2, Cleared = 3, NewCredentialObserved = 4 }

// Ordinary clients never own this transport or a Host key. Each call is a fresh bounded
// Host-initiated query; Owner intent is supplied only by trusted local authentication.
internal sealed class PeerRotationStatusRpcClient(PeerSecurityRpcRuntime runtime, IPeerHttpTransportFactory transport)
{
    internal async Task<PeerRotationStatusExchange> CheckAsync(Guid peer, Uri address, LocalPrincipalMutationActor? owner = null, CancellationToken ct = default)
    {
        if (peer == Guid.Empty || peer == runtime.HostId || !address.IsAbsoluteUri || address.Scheme != "https" ||
            address.UserInfo.Length != 0 || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException("A reachable peer HTTPS address is required.");
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var query = runtime.Repository.BeginPeerRotationStatusQuery(peer, owner);
        using var connection = transport.Create(
            pin => runtime.Authentication.AdmitHandshake(peer, pin, PeerTrafficPurpose.TrustMaintenance),
            actual => runtime.Authentication.Authenticate(peer, actual.PeerFingerprint, PeerTrafficPurpose.TrustMaintenance));
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = connection.Handler, HttpVersion = HttpVersion.Version20, HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            MaxReceiveMessageSize = PeerSecurityRpcService.MaximumMessageBytes, MaxSendMessageSize = PeerSecurityRpcService.MaximumMessageBytes
        });
        var client = new PeerSecurityProtocol.PeerSecurityProtocolClient(channel); var hello = PeerSecurityRpcRuntime.Hello(runtime.HostId);
        var reply = await client.NegotiateAsync(hello, cancellationToken: deadline.Token).ResponseAsync.ConfigureAwait(false);
        if (reply.Host is null || PeerSecurityRpcService.Id(reply.Host.HostId) != peer) throw new AuthenticationException("Peer identity refused.");
        var actual = connection.Identity;
        NegotiatedProtocol.Negotiate(hello.Handshake, reply.Handshake).Require(FeatureCapability.PeerRotationStatus);
        deadline.Token.ThrowIfCancellationRequested();
        if (HostTrustPlanning.Build(runtime.Credentials.Read()).Publication?.CurrentFingerprint != actual.LocalFingerprint)
            throw new AuthenticationException("Local Host credential changed during the exchange.");
        if (actual.PeerFingerprint == query.Request.NewFingerprint)
        {
            // Ordinary TLS observation, not status content, has already promoted the retained
            // key. Do not run an Old-only completion or clear its durable receipt identity.
            runtime.Authentication.Authenticate(peer, actual.PeerFingerprint, PeerTrafficPurpose.TrustMaintenance);
            return PeerRotationStatusExchange.NewCredentialObserved;
        }
        var status = await client.ReadRotationStatusAsync(PeerRotationStatusWire.Wire(query.Request), cancellationToken: deadline.Token).ResponseAsync.ConfigureAwait(false);
        deadline.Token.ThrowIfCancellationRequested();
        return runtime.Repository.CompletePeerRotationStatusQuery(query, PeerRotationStatusWire.Durable(status), actual.PeerFingerprint, actual.LocalFingerprint) switch
        {
            PeerRotationResolution.Unchanged => PeerRotationStatusExchange.Unchanged,
            PeerRotationResolution.Renewed => PeerRotationStatusExchange.Renewed,
            PeerRotationResolution.Cleared => PeerRotationStatusExchange.Cleared,
            _ => throw new AuthenticationException("Peer rotation status refused.")
        };
    }
}
