using System.Net;
using System.Security.Authentication;
using Grpc.Net.Client;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using WireResult=PalworldServerManager.Contracts.Wire.PeerRecoveryCompletionResult;

namespace PalworldServerManager.Host;

internal enum PeerRecoveryPullExchange { Confirmed=1,NoPending=2,KeyMismatch=3,Unsupported=4 }

internal sealed partial class PeerRecoveryCompletionRpcClient
{
    // The caller owns the Host generation/borrowed key until both transports fully drain.
    // No automatic trigger: denied ordinary administration must still make no contact.
    internal async Task<PeerRecoveryPullExchange> PullAsync(Guid peer,Uri address,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if(peer==Guid.Empty||peer==runtime.HostId||!address.IsAbsoluteUri||address.Scheme!="https"||
            address.UserInfo.Length!=0||address.AbsolutePath!="/"||address.Query.Length!=0||address.Fragment.Length!=0)
            throw new ArgumentException("A peer HTTPS address is required.");
        ct.ThrowIfCancellationRequested();using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(15));
        PeerGrantMutationActor expected;PeerRecoveryCompletionReply receipt;Guid approval;
        using(var first=new PullConnection(runtime,transport,peer,address,deadline.Token))
        {
            if(!await first.Negotiate().ConfigureAwait(false))return PeerRecoveryPullExchange.Unsupported;
            var offer=await first.Rpc.ReadRecoveryCompletionOfferAsync(new(){OfferingHostId=peer.ToString("D")},cancellationToken:deadline.Token).ResponseAsync.ConfigureAwait(false);
            first.RequireCurrent();PeerRecoveryOfferWire.ValidateOffer(offer,peer,runtime.HostId);
            if(offer.Acknowledgment is not {} ack)return PeerRecoveryPullExchange.NoPending;
            approval=PeerRecoveryCompletionWire.ValidateRequest(ack).ApprovalId;
            var result=runtime.ReceiveAuthenticatedRecoveryCompletion(first.Proof,approval,ack.AcknowledgedFingerprint,deadline.Token);
            if(result.Disposition==RecoveryCompletionDisposition.KeyMismatch)return PeerRecoveryPullExchange.KeyMismatch;
            receipt=new() {ReceivingHostId=runtime.HostId.ToString("D"),ApprovingHostId=peer.ToString("D"),ApprovalId=approval.ToString("D"),
                AcknowledgedFingerprint=ack.AcknowledgedFingerprint,Result=result.Disposition switch
                {
                    RecoveryCompletionDisposition.Recorded=>WireResult.Recorded,
                    RecoveryCompletionDisposition.AlreadyRecorded=>WireResult.AlreadyRecorded,
                    _=>throw new InvalidOperationException("Unknown recovery receipt result.")
                }};
            expected=first.Proof with{Incarnation=result.Incarnation};
        }
        // Receiving may advance only our local incarnation. Never reuse or refresh the
        // first TLS session, and never accept a later unrelated incarnation or receipt.
        using var second=new PullConnection(runtime,transport,peer,address,deadline.Token);
        if(!await second.Negotiate().ConfigureAwait(false))return PeerRecoveryPullExchange.Unsupported;
        if(second.Proof!=expected)throw new AuthenticationException("Recovery relationship changed between connections.");
        runtime.RequireRecordedRecoveryCompletion(second.Proof,approval,deadline.Token);
        var confirmed=await second.Rpc.ConfirmRecoveryCompletionOfferAsync(receipt,cancellationToken:deadline.Token).ResponseAsync.ConfigureAwait(false);
        second.RequireCurrent();runtime.RequireRecordedRecoveryCompletion(second.Proof,approval,deadline.Token);
        PeerRecoveryOfferWire.ValidateConfirmation(confirmed,receipt);return PeerRecoveryPullExchange.Confirmed;
    }

    private sealed class PullConnection:IDisposable
    {
        private readonly PeerSecurityRpcRuntime local;private readonly Guid peer;private readonly CancellationToken ct;
        private readonly IPeerHttpTransport connection;private readonly GrpcChannel channel;private PeerGrantMutationActor? proof;
        internal PeerGrantMutationActor Proof=>proof??throw new AuthenticationException("Completed recovery TLS proof required.");
        internal PeerSecurityProtocol.PeerSecurityProtocolClient Rpc{get;}
        internal PullConnection(PeerSecurityRpcRuntime local,IPeerHttpTransportFactory factory,Guid peer,Uri address,CancellationToken ct)
        {
            this.local=local;this.peer=peer;this.ct=ct;
            connection=factory.Create(pin=>local.Repository.Read(peer) is {State:"Active"} trust&&trust.CurrentFingerprint==pin,actual=>
            {
                var incarnation=local.Repository.ReadRecoveryRelationshipIncarnation(peer,actual.PeerFingerprint,actual.LocalFingerprint,ct);
                var captured=new PeerGrantMutationActor(local.HostId,peer,actual.PeerFingerprint,actual.LocalFingerprint,incarnation);
                if(Interlocked.CompareExchange(ref proof,captured,null) is not null)throw new AuthenticationException("Fresh recovery connection required.");
            });
            try
            {
                channel=GrpcChannel.ForAddress(address,new GrpcChannelOptions {HttpHandler=connection.Handler,HttpVersion=HttpVersion.Version20,
                    HttpVersionPolicy=HttpVersionPolicy.RequestVersionExact,MaxReceiveMessageSize=PeerSecurityRpcService.MaximumMessageBytes,MaxSendMessageSize=PeerSecurityRpcService.MaximumMessageBytes});
                Rpc=new(channel);
            }
            catch{connection.Dispose();throw;}
        }
        internal async Task<bool> Negotiate()
        {
            var hello=PeerSecurityRpcRuntime.RecoveryHello(local.HostId);
            var response=await Rpc.NegotiateRecoveryAsync(hello,cancellationToken:ct).ResponseAsync.ConfigureAwait(false);
            if(response.Host is null||PeerSecurityRpcService.Id(response.Host.HostId)!=peer||response.Handshake is null||
                response.Handshake.Capabilities.Count>64||response.Handshake.ProductVersion.Length>256)throw new AuthenticationException("Recovery peer negotiation refused.");
            var negotiated=NegotiatedProtocol.Negotiate(hello.Handshake,response.Handshake);negotiated.Require(FeatureCapability.PeerRecoveryCompletion);
            RequireCurrent();return negotiated.Supports(FeatureCapability.PeerRecoveryCompletionOffer);
        }
        internal void RequireCurrent()
        {
            ct.ThrowIfCancellationRequested();var actual=connection.Identity;var original=Proof;
            if(actual.PeerFingerprint!=original.PeerFingerprint||actual.LocalFingerprint!=original.LocalFingerprint||
                local.Repository.ReadRecoveryRelationshipIncarnation(peer,actual.PeerFingerprint,actual.LocalFingerprint,ct)!=original.Incarnation)
                throw new AuthenticationException("Recovery connection identity changed.");
        }
        public void Dispose(){try{channel.Dispose();}finally{connection.Dispose();}}
    }
}
