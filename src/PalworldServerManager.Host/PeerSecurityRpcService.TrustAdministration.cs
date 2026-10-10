using System.Security.Authentication;
using Google.Protobuf;
using Grpc.Core;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

public sealed partial class PeerSecurityRpcService
{
    // Ordinary machine ceiling only. The sending Host independently checks its local user.
    private async Task<T> DispatchTrustAdministration<T>(ServerCallContext context,Func<PeerPermissionCall,T> action)
    {
        try
        {
            return await runtime.Permissions.Invoke(context.GetHttpContext(),action,context.CancellationToken,
                FeatureCapability.PeerTrustAdministration).ConfigureAwait(false);
        }
        catch(OperationCanceledException){throw new RpcException(new(StatusCode.Cancelled,"Peer request canceled."));}
        catch(AuthenticationException){throw new RpcException(new(StatusCode.Unauthenticated,"Peer authentication refused."));}
        catch(UnauthorizedAccessException){throw new RpcException(new(StatusCode.PermissionDenied,"Peer trust permission refused."));}
        catch(StaleAuthorizationRevisionException){throw new RpcException(new(StatusCode.Aborted,"Permission policy changed; refresh before retrying."));}
        catch(ArgumentException){throw new RpcException(new(StatusCode.InvalidArgument,"Invalid peer trust request."));}
        catch(InvalidOperationException){throw new RpcException(new(StatusCode.FailedPrecondition,"Peer trust precondition failed."));}
        catch(Exception ex)when(ex is not OutOfMemoryException){throw new RpcException(new(StatusCode.Internal,"Peer trust request failed."));}
    }
    public override Task<PeerTrustPageReply> ReadManagedPeerTrust(PeerTrustPageRequest request,ServerCallContext context)
        =>DispatchTrustAdministration(context,call=>
        {
            if(Id(request.AuthoritativeHostId)!=runtime.HostId||request.Limit>16)throw new ArgumentException();
            var page=call.ReadPeerTrust(request.AfterPeerHostId.Length==0?null:Id(request.AfterPeerHostId),request.Limit==0?16:(int)request.Limit);
            var reply=new PeerTrustPageReply{AuthoritativeHostId=page.HostId.ToString("D"),AuthorizationRevision=page.Revision,
                NextAfterPeerHostId=page.NextAfterPeer?.ToString("D")??""};
            foreach(var peer in page.Peers)reply.Peers.Add(new PeerManagedTrustItem{PeerHostId=peer.PeerHostId.ToString("D"),
                State=peer.State switch{"PeerBound"=>PeerManagedTrustState.PeerBound,"Active"=>PeerManagedTrustState.Active,"Revoked"=>PeerManagedTrustState.Revoked,_=>throw new InvalidOperationException()},
                Incarnation=peer.Incarnation,CurrentFingerprint=peer.CurrentFingerprint??"",PendingFingerprint=peer.PendingFingerprint??"",RecoveryRequired=peer.RecoveryRequired});
            if(reply.CalculateSize()>MaximumMessageBytes)throw new InvalidOperationException();return reply;
        });
    public override Task<PeerRevokeTrustReply> RevokeManagedPeerTrust(PeerRevokeTrustRequest request,ServerCallContext context)
        =>DispatchTrustAdministration(context,call=>
        {
            if(Id(request.AuthoritativeHostId)!=runtime.HostId||request.ExpectedAuthorizationRevision<0||request.ExpectedIncarnation<=0)throw new ArgumentException();
            var result=call.RevokePeer(request.ExpectedAuthorizationRevision,Id(request.PeerHostId),request.ExpectedIncarnation);
            return new PeerRevokeTrustReply{AuthoritativeHostId=runtime.HostId.ToString("D"),PeerHostId=result.PeerHostId.ToString("D"),
                AuthorizationRevision=result.Revision,Incarnation=result.Incarnation,Changed=result.Changed};
        });
}
