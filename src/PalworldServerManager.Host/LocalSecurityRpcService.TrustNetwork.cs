using System.Security.Authentication;
using Grpc.Core;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.Host;

public sealed partial class LocalSecurityRpcService
{
    private Task<T> DispatchTrustNetwork<T>(LocalPeerContactRequest request,ServerCallContext context,FeatureCapability feature,
        Func<HostNetworkGeneration,LocalPrincipalConnectionAuthentication,Guid,HostReachableAddress,CancellationToken,Task<T>> action)
        =>Dispatch(context,false,false,async session=>
        {
            if(!session.BelongsTo(runtime))throw new AuthenticationException();
            session.Protocol!.Require(feature);
            var generation=runtime.Pairing??throw new InvalidOperationException("Peer network is not configured.");
            if(request.PeerPort is 0 or >65535)throw new ArgumentException();
            var peer=Id(request.PeerHostId);var address=HostReachableAddress.Parse(request.ReachableHost,(int)request.PeerPort);
            try{return await action(generation,session.Authentication,peer,address,context.CancellationToken).ConfigureAwait(false);}
            catch(UnauthorizedAccessException){throw new RpcException(new(StatusCode.PermissionDenied,"Local trust permission refused."));}
            catch(RpcException)when(context.CancellationToken.IsCancellationRequested){throw new OperationCanceledException(context.CancellationToken);}
            catch(RpcException){throw new RpcException(new(StatusCode.Unavailable,"Peer trust contact did not complete."));}
        });
    public override Task<LocalPreparePeerUnpairReply> PreparePeerUnpair(LocalPeerContactRequest request,ServerCallContext context)
        =>DispatchTrustNetwork(request,context,FeatureCapability.LocalPeerUnpairPreparation,async(generation,connection,peer,address,ct)=>
            new LocalPreparePeerUnpairReply{Prepared=await runtime.TrustNetwork.Prepare(generation,connection,peer,address,ct).ConfigureAwait(false)});
    public override Task<LocalCompletePeerRecoveryReply> CompletePeerRecovery(LocalPeerContactRequest request,ServerCallContext context)
        =>DispatchTrustNetwork(request,context,FeatureCapability.LocalOwnerPeerRecovery,async(generation,connection,peer,address,ct)=>
        {
            var result=await runtime.TrustNetwork.CompleteRecovery(generation,connection,peer,address,ct).ConfigureAwait(false);
            return new LocalCompletePeerRecoveryReply{
                Sent=result.Sent switch{PeerRecoveryCompletionExchange.Confirmed=>LocalPeerRecoveryPushResult.Confirmed,PeerRecoveryCompletionExchange.NoPending=>LocalPeerRecoveryPushResult.NoPending,PeerRecoveryCompletionExchange.KeyMismatch=>LocalPeerRecoveryPushResult.KeyMismatch,_=>throw new InvalidOperationException()},
                Received=result.Received switch{PeerRecoveryPullExchange.Confirmed=>LocalPeerRecoveryPullResult.Confirmed,PeerRecoveryPullExchange.NoPending=>LocalPeerRecoveryPullResult.NoPending,PeerRecoveryPullExchange.KeyMismatch=>LocalPeerRecoveryPullResult.KeyMismatch,PeerRecoveryPullExchange.Unsupported=>LocalPeerRecoveryPullResult.Unsupported,_=>throw new InvalidOperationException()}};
        });
}
