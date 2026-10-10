using System.Security.Authentication;
using Grpc.Core;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.Host;

public sealed partial class LocalSecurityRpcService
{
    private Task<T> DispatchRemoteTrust<T>(LocalPeerContactRequest? destination,ServerCallContext context,
        Func<HostNetworkGeneration,LocalPrincipalConnectionAuthentication,Guid,HostReachableAddress,CancellationToken,Task<T>> action)
        =>Dispatch(context,false,false,async session=>
        {
            if(!session.BelongsTo(runtime))throw new AuthenticationException();
            session.Protocol!.Require(FeatureCapability.LocalRemoteTrustAdministration);
            var generation=runtime.Pairing??throw new InvalidOperationException();
            if(destination is null||destination.PeerPort is 0 or >65535)throw new ArgumentException();
            var peer=Id(destination.PeerHostId);var address=HostReachableAddress.Parse(destination.ReachableHost,(int)destination.PeerPort);
            try{return await action(generation,session.Authentication,peer,address,context.CancellationToken).ConfigureAwait(false);}
            catch(UnauthorizedAccessException){throw new RpcException(new(StatusCode.PermissionDenied,"Local remote-trust permission refused."));}
            catch(RpcException)when(context.CancellationToken.IsCancellationRequested){throw new OperationCanceledException(context.CancellationToken);}
            catch(RpcException ex)
            {
                var code=ex.StatusCode switch{StatusCode.PermissionDenied=>StatusCode.PermissionDenied,StatusCode.Aborted=>StatusCode.Aborted,
                    StatusCode.FailedPrecondition=>StatusCode.FailedPrecondition,StatusCode.InvalidArgument=>StatusCode.InvalidArgument,
                    _=>StatusCode.Unavailable};
                throw new RpcException(new(code,"Remote trust request refused or did not complete."));
            }
        });
    public override Task<PeerTrustPageReply> ReadRemotePeerTrust(LocalRemotePeerTrustPageRequest request,ServerCallContext context)
        =>DispatchRemoteTrust(request.Destination,context,(generation,connection,peer,address,ct)=>
        {
            if(request.Limit>16)throw new ArgumentException();var after=request.AfterPeerHostId.Length==0?(Guid?)null:Id(request.AfterPeerHostId);
            return runtime.TrustNetwork.ReadRemote(generation,connection,peer,address,after,request.Limit==0?16:(int)request.Limit,ct);
        });
    public override Task<PeerRevokeTrustReply> RevokeRemotePeerTrust(LocalRemoteRevokePeerRequest request,ServerCallContext context)
        =>DispatchRemoteTrust(request.Destination,context,(generation,connection,peer,address,ct)=>
        {
            if(request.ExpectedAuthorizationRevision<0||request.ExpectedIncarnation<=0)throw new ArgumentException();var target=Id(request.PeerHostId);
            if(target==peer)throw new ArgumentException();
            return runtime.TrustNetwork.RevokeRemote(generation,connection,peer,address,target,request.ExpectedAuthorizationRevision,request.ExpectedIncarnation,ct);
        });
}
