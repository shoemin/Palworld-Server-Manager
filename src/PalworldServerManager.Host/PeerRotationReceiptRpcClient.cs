using System.Net;
using System.Security.Authentication;
using Grpc.Net.Client;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

internal enum PeerRotationReceiptExchange { Confirmed = 1, NoReceiptPending = 2 }

internal sealed class PeerRotationReceiptRpcClient(PeerSecurityRpcRuntime runtime, IPeerHttpTransportFactory transport)
{
    internal async Task<PeerRotationReceiptExchange> ConfirmAsync(Guid peer, Uri address, CancellationToken ct = default)
    {
        if (peer == Guid.Empty || peer == runtime.HostId || !address.IsAbsoluteUri || address.Scheme != "https" ||
            address.UserInfo.Length != 0 || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException("A reachable peer HTTPS address is required.");
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
        NegotiatedProtocol.Negotiate(hello.Handshake, negotiated.Handshake).Require(FeatureCapability.PeerRotationReceipt);
        var actual = connection.Identity;
        var pending = runtime.Repository.ReadPendingPeerRotationReceipt(peer, actual.PeerFingerprint, actual.LocalFingerprint);
        if (pending is null) return PeerRotationReceiptExchange.NoReceiptPending;
        var request = PeerRotationReceiptWire.Wire(pending);
        var reply = await client.ConfirmRotationPromotionAsync(request, cancellationToken: deadline.Token).ResponseAsync.ConfigureAwait(false);
        deadline.Token.ThrowIfCancellationRequested();
        if (reply.Request is null || !reply.Request.Equals(request) ||
            reply.Result is not (PeerRotationReceiptResult.Recorded or PeerRotationReceiptResult.AlreadyRecorded))
            throw new AuthenticationException("Peer promotion receipt refused.");
        runtime.Repository.ConfirmPeerRotationReceipt(peer, actual.PeerFingerprint, pending.RotationId, actual.LocalFingerprint);
        return PeerRotationReceiptExchange.Confirmed;
    }
}

internal static class PeerRotationReceiptWire
{
    internal static RoutineRotationPromotionReceipt Durable(PeerRotationReceiptRequest request) =>
        new(PeerSecurityRpcService.Id(request.RequestId), PeerSecurityRpcService.Id(request.HostId), PeerSecurityRpcService.Id(request.RotationId), request.NewFingerprint);
    internal static PeerRotationReceiptRequest Wire(RoutineRotationPromotionReceipt receipt) => new()
    { RequestId = receipt.RequestId.ToString("D"), HostId = receipt.HostId.ToString("D"), RotationId = receipt.RotationId.ToString("D"), NewFingerprint = receipt.NewFingerprint };
}
