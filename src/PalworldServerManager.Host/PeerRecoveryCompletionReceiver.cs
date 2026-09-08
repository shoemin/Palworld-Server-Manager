using System.Security.Authentication;
using Microsoft.AspNetCore.Http;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

// Fixed intrinsic security surface, owned by one runtime and actual TLS connection.
internal sealed class PeerRecoveryCompletionReceiver(PeerSecurityRpcRuntime runtime,GrantPolicyRepository repository)
{
    private PeerGrantMutationActor OfferProof(PeerSecurityRpcConnection session)
    {
        if(session.Protocol is null)throw new InvalidOperationException("Negotiate this connection first.");
        session.Protocol.Require(FeatureCapability.PeerRecoveryCompletion);
        session.Protocol.Require(FeatureCapability.PeerRecoveryCompletionOffer);
        return new(runtime.HostId,session.PeerId,session.PeerFingerprint,session.LocalFingerprint,session.PeerIncarnation);
    }
    internal Task<PeerRecoveryOfferReply> Offer(HttpContext context,PeerRecoveryOfferRequest request,CancellationToken ct)
        =>Invoke(context,ct,(session,token)=>
        {
            var proof=OfferProof(session);
            if(PeerRecoveryOfferWire.ValidateRequest(request)!=runtime.HostId)throw new AuthenticationException("Recovery offering Host refused.");
            var pending=repository.ReadPendingRecoveryCompletion(proof,token);
            var reply=new PeerRecoveryOfferReply {OfferingHostId=runtime.HostId.ToString("D")};
            if(pending is not null)reply.Acknowledgment=new() {ReceivingHostId=session.PeerId.ToString("D"),
                ApprovalId=pending.ApprovalId.ToString("D"),AcknowledgedFingerprint=pending.ApprovedPeerFingerprint};
            return reply;
        });
    internal Task<PeerRecoveryOfferConfirmationReply> ConfirmOffer(HttpContext context,PeerRecoveryCompletionReply request,CancellationToken ct)
        =>Invoke(context,ct,(session,token)=>
        {
            var proof=OfferProof(session);
            var approval=PeerRecoveryOfferWire.ValidatePositiveReceipt(request,runtime.HostId,session.PeerId,session.PeerFingerprint);
            var result=repository.ConfirmAuthenticatedRecoveryCompletion(proof,approval,token);
            return new PeerRecoveryOfferConfirmationReply {Receipt=request.Clone(),Result=result.Changed
                ?PeerRecoveryOfferConfirmationResult.Confirmed:PeerRecoveryOfferConfirmationResult.AlreadyConfirmed};
        });
    private async Task<T> Invoke<T>(HttpContext context,CancellationToken ct,Func<PeerSecurityRpcConnection,CancellationToken,T> action)
    {
        PermissionDispatchChannel.Require(context);
        var connection=context.Features.Get<PeerSecurityRpcConnection>();
        if(connection is null||!connection.BelongsTo(runtime))throw new AuthenticationException("Peer connection refused.");
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(ct,context.RequestAborted);
        return await connection.Invoke(session=>action(session,cancellation.Token),cancellation.Token).ConfigureAwait(false);
    }
    internal Task<PeerHello> Negotiate(HttpContext context,PeerHello request,CancellationToken ct)
        =>Invoke(context,ct,(session,token)=>
        {
            if(session.NegotiationAttempted)throw new InvalidOperationException("Connection negotiation already attempted.");
            session.NegotiationAttempted=true;
            if(request.Handshake is null||request.Host is null||request.Handshake.Capabilities.Count>64||
                request.Handshake.ProductVersion.Length>256)throw new ArgumentException("Invalid recovery hello.");
            var peer=PeerSecurityRpcService.Id(request.Host.HostId);
            var hello=PeerSecurityRpcRuntime.RecoveryHello(runtime.HostId);
            var protocol=NegotiatedProtocol.Negotiate(hello.Handshake,request.Handshake);
            protocol.Require(FeatureCapability.PeerRecoveryCompletion);
            var incarnation=runtime.Repository.ReadRecoveryRelationshipIncarnation(peer,session.PeerFingerprint,session.LocalFingerprint,token);
            session.PeerId=peer;session.PeerIncarnation=incarnation;session.Protocol=protocol;
            hello.Handshake.Protocol.Minor=protocol.Minor;return hello;
        });
    internal Task<PeerRecoveryCompletionReply> Receive(HttpContext context,PeerRecoveryCompletionRequest request,CancellationToken ct)
        =>Invoke(context,ct,(session,token)=>
        {
            if(session.Protocol is null)throw new InvalidOperationException("Negotiate this connection first.");
            session.Protocol.Require(FeatureCapability.PeerRecoveryCompletion);
            var (recipient,approval)=PeerRecoveryCompletionWire.ValidateRequest(request);
            if(recipient!=runtime.HostId)throw new AuthenticationException("Recovery recipient refused.");
            var proof=new PeerGrantMutationActor(runtime.HostId,session.PeerId,session.PeerFingerprint,session.LocalFingerprint,session.PeerIncarnation);
            var result=repository.ReceiveAuthenticatedRecoveryCompletion(proof,approval,request.AcknowledgedFingerprint,token);
            return new PeerRecoveryCompletionReply {ReceivingHostId=runtime.HostId.ToString("D"),ApprovingHostId=session.PeerId.ToString("D"),
                ApprovalId=approval.ToString("D"),AcknowledgedFingerprint=request.AcknowledgedFingerprint,Result=result.Disposition switch
                {
                    RecoveryCompletionDisposition.Recorded=>Contracts.Wire.PeerRecoveryCompletionResult.Recorded,
                    RecoveryCompletionDisposition.AlreadyRecorded=>Contracts.Wire.PeerRecoveryCompletionResult.AlreadyRecorded,
                    RecoveryCompletionDisposition.KeyMismatch=>Contracts.Wire.PeerRecoveryCompletionResult.KeyMismatch,
                    _=>throw new InvalidOperationException("Unknown recovery result.")
                }};
        });
}
