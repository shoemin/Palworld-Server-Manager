using System.Net;
using System.Security.Authentication;
using Grpc.Net.Client;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

internal sealed class PeerCurrentCredentialRpcClient(PeerSecurityRpcRuntime runtime, IPeerHttpTransportFactory transport)
{
    internal async Task<bool> ConfirmAsync(Guid peer, Uri address, Guid rotation, CancellationToken ct = default)
    {
        if (peer == Guid.Empty || peer == runtime.HostId || rotation == Guid.Empty || !address.IsAbsoluteUri || address.Scheme != "https" ||
            address.UserInfo.Length != 0 || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException("A reachable peer HTTPS address and rotation are required.");
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var connection = transport.Create(
            pin => runtime.Authentication.AdmitHandshake(peer, pin, PeerTrafficPurpose.TrustMaintenance),
            actual => runtime.Authentication.Authenticate(peer, actual.PeerFingerprint, PeerTrafficPurpose.TrustMaintenance));
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = connection.Handler, HttpVersion = HttpVersion.Version20, HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            MaxReceiveMessageSize = PeerSecurityRpcService.MaximumMessageBytes, MaxSendMessageSize = PeerSecurityRpcService.MaximumMessageBytes
        });
        var client = new PeerSecurityProtocol.PeerSecurityProtocolClient(channel); var hello = PeerSecurityRpcRuntime.Hello(runtime.HostId);
        var negotiated = await client.NegotiateAsync(hello, cancellationToken: deadline.Token).ResponseAsync.ConfigureAwait(false);
        if (negotiated.Host is null || PeerSecurityRpcService.Id(negotiated.Host.HostId) != peer) throw new AuthenticationException("Peer identity refused.");
        NegotiatedProtocol.Negotiate(hello.Handshake, negotiated.Handshake).Require(FeatureCapability.PeerCurrentCredentialConfirmation);
        var actual = connection.Identity;
        _=runtime.AuthenticatedRecoveryContact(peer,address,actual,deadline.Token);
        var incarnation = runtime.Repository.ReadAuthenticatedRelationshipIncarnation(peer, actual.PeerFingerprint, actual.LocalFingerprint);
        var proof = runtime.Credentials.PrepareCurrentCredentialConfirmation(rotation, actual.LocalFingerprint);
        var request = PeerCurrentCredentialWire.Wire(proof);
        var reply = await client.ConfirmCurrentCredentialAsync(request, cancellationToken: deadline.Token).ResponseAsync.ConfigureAwait(false);
        deadline.Token.ThrowIfCancellationRequested();
        if (reply.Request is null || !reply.Request.Equals(request) || reply.Result != PeerCurrentCredentialResult.Confirmed)
            throw new AuthenticationException("Peer current credential confirmation refused.");
        return runtime.Credentials.RecordCurrentCredentialConfirmation(proof, peer, actual.PeerFingerprint, actual.LocalFingerprint, incarnation, deadline.Token);
    }
}

internal static class PeerCurrentCredentialWire
{
    internal static RoutineRotationCredentialConfirmation Durable(PeerCurrentCredentialRequest request) =>
        new(PeerSecurityRpcService.Id(request.RequestId), PeerSecurityRpcService.Id(request.HostId), PeerSecurityRpcService.Id(request.RotationId), request.NewFingerprint);
    internal static PeerCurrentCredentialRequest Wire(RoutineRotationCredentialConfirmation proof) => new()
    { RequestId = proof.RequestId.ToString("D"), HostId = proof.HostId.ToString("D"), RotationId = proof.RotationId.ToString("D"), NewFingerprint = proof.NewFingerprint };
}
