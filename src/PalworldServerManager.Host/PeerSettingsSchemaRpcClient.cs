using System.Net;
using System.Security.Authentication;
using Grpc.Net.Client;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

// Fixed Host-only read. Both the current local user and independent peer ceiling
// are required; no client receives the machine certificate or peer transport.
internal sealed class PeerSettingsSchemaRpcClient(PeerSecurityRpcRuntime runtime, IPeerHttpTransportFactory transport)
{
    internal async Task<SettingsSchemaReply> Read(PalworldServerManager.Contracts.Wire.ServerRef server, Uri address,
        Action<CancellationToken> authorize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authorize); ArgumentNullException.ThrowIfNull(address);
        var target = SettingsSchemaReader.Target(server); var peer = target.AuthoritativeHostId;
        if (peer == runtime.HostId || !address.IsAbsoluteUri || address.Scheme != "https" || address.UserInfo.Length != 0 ||
            address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0) throw new ArgumentException();
        var request = new SettingsSchemaRequest { Server = server.Clone() };
        ct.ThrowIfCancellationRequested(); authorize(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        PeerGrantMutationActor? original = null;
        using var connection = transport.Create(pin => runtime.Authentication.AdmitHandshake(peer, pin, PeerTrafficPurpose.OrdinaryManagement), actual =>
        {
            var incarnation = runtime.Repository.ReadAuthenticatedRelationshipIncarnation(peer, actual.PeerFingerprint, actual.LocalFingerprint);
            var proof = new PeerGrantMutationActor(runtime.HostId, peer, actual.PeerFingerprint, actual.LocalFingerprint, incarnation);
            if (Interlocked.CompareExchange(ref original, proof, null) is not null) throw new AuthenticationException("Fresh peer connection required.");
            runtime.Authentication.AuthenticateOrdinary(proof, deadline.Token);
        });
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = connection.Handler, HttpVersion = HttpVersion.Version20,
            HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact, MaxReceiveMessageSize = SettingsSchemaValidation.MaximumBytes,
            MaxSendMessageSize = SettingsSchemaValidation.MaximumBytes });
        var client = new PeerSecurityProtocol.PeerSecurityProtocolClient(channel); var hello = PeerSecurityRpcRuntime.Hello(runtime.HostId);
        var negotiated = await client.NegotiateAsync(hello, cancellationToken: deadline.Token).ResponseAsync.ConfigureAwait(false);
        if (negotiated.Host is null || negotiated.Host.HostId != peer.ToString("D") || negotiated.Handshake is null ||
            negotiated.Handshake.Capabilities.Count > 64 || negotiated.Handshake.ProductVersion.Length > 256)
            throw new AuthenticationException("Peer negotiation refused.");
        var protocol = NegotiatedProtocol.Negotiate(hello.Handshake, negotiated.Handshake);
        protocol.Require(FeatureCapability.PeerTrustActivation); protocol.Require(FeatureCapability.SettingsSchema);
        void RequireProof()
        {
            deadline.Token.ThrowIfCancellationRequested(); var proof = original ?? throw new AuthenticationException(); var actual = connection.Identity;
            if (actual.PeerFingerprint != proof.PeerFingerprint || actual.LocalFingerprint != proof.LocalFingerprint) throw new AuthenticationException();
            runtime.Authentication.AuthenticateOrdinary(proof, deadline.Token);
        }
        RequireProof(); authorize(deadline.Token); deadline.Token.ThrowIfCancellationRequested();
        var result = await client.GetSettingsSchemaAsync(request, cancellationToken: deadline.Token).ResponseAsync.ConfigureAwait(false);
        RequireProof(); authorize(deadline.Token); ValidateReply(result, request); deadline.Token.ThrowIfCancellationRequested(); return result;
    }
    internal static void ValidateReply(SettingsSchemaReply reply, SettingsSchemaRequest request)
    {
        if (!SettingsSchemaValidation.IsValid(reply) || !SettingsSchemaValidation.IsValidServer(request.Server) ||
            reply.Server.AuthoritativeHostId != request.Server.AuthoritativeHostId || reply.Server.ServerProfileId != request.Server.ServerProfileId)
            throw new AuthenticationException("Invalid settings schema reply.");
    }
}
