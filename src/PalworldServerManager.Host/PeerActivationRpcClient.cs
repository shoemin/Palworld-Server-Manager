using System.Net;
using System.Security.Authentication;
using Grpc.Net.Client;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

// Host-owned outbound boundary. Ordinary clients never construct this or obtain its key.
// Each bounded attempt gets a new channel/negotiation; callers retry from durable trust.
internal sealed class PeerActivationRpcClient(PeerSecurityRpcRuntime runtime, IPeerHttpTransportFactory transport)
{
    internal Task<PeerActivationDisposition> FinalizeAsync(Guid peer, Uri address, CancellationToken ct = default)
        => FinalizeCoreAsync(peer, address, null, ct);
    internal Task<PeerActivationDisposition> FinalizeForOwnerAsync(Guid peer, Uri address, LocalPrincipalMutationActor owner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner); ct.ThrowIfCancellationRequested();
        runtime.Repository.AuthorizePairingOwner(owner); ct.ThrowIfCancellationRequested();
        return FinalizeCoreAsync(peer, address, owner, ct);
    }
    private async Task<PeerActivationDisposition> FinalizeCoreAsync(Guid peer, Uri address, LocalPrincipalMutationActor? owner, CancellationToken ct)
    {
        if (peer == Guid.Empty || peer == runtime.HostId || !address.IsAbsoluteUri || address.Scheme != "https" ||
            address.UserInfo.Length != 0 || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException("A reachable peer HTTPS address is required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var connection = transport.Create(
            pin => runtime.Authentication.AdmitHandshake(peer, pin, PeerTrafficPurpose.PairingFinalization),
            actual => runtime.Authentication.Authenticate(peer, actual.PeerFingerprint, PeerTrafficPurpose.PairingFinalization));
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = connection.Handler, HttpVersion = HttpVersion.Version20, HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            MaxReceiveMessageSize = PeerSecurityRpcService.MaximumMessageBytes, MaxSendMessageSize = PeerSecurityRpcService.MaximumMessageBytes
        });
        var client = new PeerSecurityProtocol.PeerSecurityProtocolClient(channel);
        var hello = PeerSecurityRpcRuntime.Hello(runtime.HostId);
        var reply = await client.NegotiateAsync(hello, cancellationToken: deadline.Token).ResponseAsync.ConfigureAwait(false);
        if (reply.Host is null || PeerSecurityRpcService.Id(reply.Host.HostId) != peer) throw new AuthenticationException("Peer identity refused.");
        var actual = connection.Identity;
        NegotiatedProtocol.Negotiate(hello.Handshake, reply.Handshake).Require(FeatureCapability.PeerTrustActivation);
        _=runtime.AuthenticatedRecoveryContact(peer,address,actual,deadline.Token);
        var ack = owner is null
            ? runtime.Repository.PrepareActivationAcknowledgement(peer, actual.PeerFingerprint, actual.LocalFingerprint)
            : runtime.Repository.PrepareOwnerActivationAcknowledgement(owner, peer, actual.PeerFingerprint, actual.LocalFingerprint);
        var activated = await client.ActivateAsync(PeerSecurityRpcService.Wire(ack), cancellationToken: deadline.Token).ResponseAsync.ConfigureAwait(false);
        if (activated.Acknowledgement is null || activated.Result is not (PeerActivationResult.Activated or PeerActivationResult.AlreadyActive))
            throw new AuthenticationException("Peer activation proof refused.");
        deadline.Token.ThrowIfCancellationRequested();
        var acknowledgement = PeerSecurityRpcService.Durable(activated.Acknowledgement);
        return owner is null
            ? runtime.Repository.AcceptActivationAcknowledgement(peer, actual.PeerFingerprint, actual.LocalFingerprint, acknowledgement, runtime.Hook)
            : runtime.Repository.AcceptOwnerActivationAcknowledgement(owner, peer, actual.PeerFingerprint, actual.LocalFingerprint,
                acknowledgement, runtime.Hook, deadline.Token);
    }
}
