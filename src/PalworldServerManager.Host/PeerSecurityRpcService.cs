using System.Security.Authentication;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

public sealed class PeerSecurityRpcService(PeerSecurityRpcRuntime runtime) : PeerSecurityProtocol.PeerSecurityProtocolBase
{
    public const int MaximumMessageBytes = 16 * 1024;
    internal static Guid Id(string text) => Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty ? id : throw new ArgumentException();
    internal static PeerActivationAck Wire(PeerActivationAcknowledgement ack) => new()
    { FromHostId = ack.FromHostId.ToString("D"), RecordedHostId = ack.RecordedHostId.ToString("D"), RecordedFingerprint = ack.RecordedFingerprint };
    internal static PeerActivationAcknowledgement Durable(PeerActivationAck ack) => new(Id(ack.FromHostId), Id(ack.RecordedHostId), ack.RecordedFingerprint);
    private async Task<T> Dispatch<T>(ServerCallContext context, bool negotiation, Func<PeerSecurityRpcConnection, T> action)
    {
        try
        {
            var http = context.GetHttpContext();
            if (!http.Request.IsHttps || http.Request.Protocol != "HTTP/2") throw new AuthenticationException();
            var connection = http.Features.Get<PeerSecurityRpcConnection>() ?? throw new AuthenticationException();
            return await connection.Invoke(session =>
            {
                if (!negotiation)
                {
                    if (session.Protocol is null) throw new InvalidOperationException();
                    session.Protocol.Require(FeatureCapability.PeerTrustActivation);
                    runtime.Authentication.Authenticate(session.PeerId, session.PeerFingerprint, PeerTrafficPurpose.PairingFinalization);
                }
                return action(session);
            }, context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw new RpcException(new(StatusCode.Cancelled, "Peer request canceled.")); }
        catch (AuthenticationException) { throw new RpcException(new(StatusCode.Unauthenticated, "Peer authentication refused.")); }
        catch (ArgumentException) { throw new RpcException(new(StatusCode.InvalidArgument, "Invalid peer request.")); }
        catch (Exception ex) when (ex is InvalidOperationException or ProtocolCompatibilityException)
        { throw new RpcException(new(StatusCode.FailedPrecondition, "Peer request precondition failed.")); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { throw new RpcException(new(StatusCode.Internal, "Peer request failed.")); }
    }
    public override Task<PeerHello> Negotiate(PeerHello request, ServerCallContext context) => Dispatch(context, true, session =>
    {
        if (session.NegotiationAttempted) throw new InvalidOperationException(); session.NegotiationAttempted = true;
        if (request.Handshake is null || request.Host is null || request.Handshake.Capabilities.Count > 64 ||
            request.Handshake.ProductVersion.Length > 256) throw new ArgumentException();
        var peer = Id(request.Host.HostId);
        runtime.Authentication.Authenticate(peer, session.PeerFingerprint, PeerTrafficPurpose.PairingFinalization);
        var hello = PeerSecurityRpcRuntime.Hello(runtime.HostId);
        var protocol = NegotiatedProtocol.Negotiate(hello.Handshake, request.Handshake);
        protocol.Require(FeatureCapability.PeerTrustActivation);
        session.PeerId = peer; session.Protocol = protocol; hello.Handshake.Protocol.Minor = protocol.Minor; return hello;
    });
    public override Task<PeerActivationReply> Activate(PeerActivationAck request, ServerCallContext context) => Dispatch(context, false, session =>
    {
        var result = runtime.Repository.AcceptActivationAcknowledgement(session.PeerId, session.PeerFingerprint,
            session.LocalFingerprint, Durable(request), runtime.Hook);
        var ack = runtime.Repository.PrepareActivationAcknowledgement(session.PeerId, session.PeerFingerprint, session.LocalFingerprint);
        return new PeerActivationReply { Acknowledgement = Wire(ack), Result = result switch
        {
            PeerActivationDisposition.Activated => PeerActivationResult.Activated,
            PeerActivationDisposition.AlreadyActive => PeerActivationResult.AlreadyActive,
            _ => throw new InvalidOperationException()
        } };
    });
}
