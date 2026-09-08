using System.Net;
using System.Security.Authentication;
using Grpc.Net.Client;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using Result=PalworldServerManager.Contracts.Wire.PeerRecoveryCompletionResult;

namespace PalworldServerManager.Host;

internal enum PeerRecoveryCompletionExchange { Confirmed=1,NoPending=2,KeyMismatch=3 }

// One actual connection and awaited scope;
// the caller retains the Host lease and borrowed certificate through complete disposal.
internal sealed partial class PeerRecoveryCompletionRpcClient(PeerSecurityRpcRuntime runtime,IPeerHttpTransportFactory transport)
{
    internal async Task<PeerRecoveryCompletionExchange> ConfirmAsync(Guid peer,Uri address,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if(peer==Guid.Empty||peer==runtime.HostId||!address.IsAbsoluteUri||address.Scheme!="https"||
            address.UserInfo.Length!=0||address.AbsolutePath!="/"||address.Query.Length!=0||address.Fragment.Length!=0)
            throw new ArgumentException("A peer HTTPS address is required.");
        ct.ThrowIfCancellationRequested();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(15));
        PeerGrantMutationActor? proof=null;PendingPeerRecoveryCompletion? original=null;
        using var connection=transport.Create(pin=>
        {
            // Before possession proof: read-only admission of an exact current Active key.
            // Independent recovery is permitted only for this fixed intrinsic exchange.
            var trust=runtime.Repository.Read(peer);
            return trust is {State:"Active"}&&trust.CurrentFingerprint==pin;
        },actual=>
        {
            var incarnation=runtime.Repository.ReadRecoveryRelationshipIncarnation(peer,actual.PeerFingerprint,actual.LocalFingerprint,deadline.Token);
            var authenticated=new PeerGrantMutationActor(runtime.HostId,peer,actual.PeerFingerprint,actual.LocalFingerprint,incarnation);
            var pending=runtime.ReadPendingRecoveryCompletion(authenticated,deadline.Token);
            if(Interlocked.CompareExchange(ref proof,authenticated,null) is not null)throw new AuthenticationException("Fresh recovery connection required.");
            original=pending;
        });
        using var channel=GrpcChannel.ForAddress(address,new GrpcChannelOptions
        {
            HttpHandler=connection.Handler,HttpVersion=HttpVersion.Version20,HttpVersionPolicy=HttpVersionPolicy.RequestVersionExact,
            MaxReceiveMessageSize=PeerSecurityRpcService.MaximumMessageBytes,MaxSendMessageSize=PeerSecurityRpcService.MaximumMessageBytes
        });
        var client=new PeerSecurityProtocol.PeerSecurityProtocolClient(channel);var hello=PeerSecurityRpcRuntime.RecoveryHello(runtime.HostId);
        var negotiated=await client.NegotiateRecoveryAsync(hello,cancellationToken:deadline.Token).ResponseAsync.ConfigureAwait(false);
        if(negotiated.Host is null||PeerSecurityRpcService.Id(negotiated.Host.HostId)!=peer||negotiated.Handshake is null||
            negotiated.Handshake.Capabilities.Count>64||negotiated.Handshake.ProductVersion.Length>256)
            throw new AuthenticationException("Recovery peer negotiation refused.");
        NegotiatedProtocol.Negotiate(hello.Handshake,negotiated.Handshake).Require(FeatureCapability.PeerRecoveryCompletion);
        var authenticatedProof=proof??throw new AuthenticationException("Completed recovery TLS proof required.");
        void RequireIdentity()
        {
            var actual=connection.Identity;
            if(actual.PeerFingerprint!=authenticatedProof.PeerFingerprint||actual.LocalFingerprint!=authenticatedProof.LocalFingerprint)
                throw new AuthenticationException("Recovery connection identity changed.");
        }
        RequireIdentity();deadline.Token.ThrowIfCancellationRequested();
        var pending=runtime.ReadPendingRecoveryCompletion(authenticatedProof,deadline.Token);
        if(pending is null)return PeerRecoveryCompletionExchange.NoPending;
        if(pending!=original)throw new AuthenticationException("Recovery approval changed during negotiation.");
        var request=new PeerRecoveryCompletionRequest {ReceivingHostId=peer.ToString("D"),ApprovalId=pending.ApprovalId.ToString("D"),AcknowledgedFingerprint=pending.ApprovedPeerFingerprint};
        var reply=await client.ReceiveRecoveryCompletionAsync(request,cancellationToken:deadline.Token).ResponseAsync.ConfigureAwait(false);
        deadline.Token.ThrowIfCancellationRequested();RequireIdentity();
        PeerRecoveryCompletionWire.ValidateReply(reply,request,runtime.HostId);
        if(reply.Result==Result.KeyMismatch)
        {
            runtime.ReadPendingRecoveryCompletion(authenticatedProof,deadline.Token);
            return PeerRecoveryCompletionExchange.KeyMismatch;
        }
        // Positive closed reply was correlated above. Canonical writer revalidates original
        // actual keys/incarnation and exact original approval before its durable audit.
        runtime.ConfirmAuthenticatedRecoveryCompletion(authenticatedProof,pending.ApprovalId,deadline.Token);
        return PeerRecoveryCompletionExchange.Confirmed;
    }
}
