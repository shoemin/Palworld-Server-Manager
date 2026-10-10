using System.Security.Authentication;
using Google.Protobuf;
using Grpc.Core;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

public sealed partial class LocalSecurityRpcService
{
    // Enter the owned connection exactly once; never nest Dispatch with Permissions.Invoke.
    private async Task<T> DispatchTrust<T>(ServerCallContext context,Func<LocalPermissionCall,T> action,
        FeatureCapability feature=FeatureCapability.LocalPeerTrustManagement)
    {
        try
        {
            return await runtime.Permissions.Invoke(context.GetHttpContext(),action,context.CancellationToken,
                feature).ConfigureAwait(false);
        }
        catch(OperationCanceledException){throw new RpcException(new(StatusCode.Cancelled,"Local request canceled."));}
        catch(AuthenticationException){throw new RpcException(new(StatusCode.Unauthenticated,"Local authentication refused."));}
        catch(UnauthorizedAccessException){throw new RpcException(new(StatusCode.PermissionDenied,"Local trust permission refused."));}
        catch(StaleAuthorizationRevisionException){throw new RpcException(new(StatusCode.Aborted,"Permission policy changed; refresh before retrying."));}
        catch(ArgumentException){throw new RpcException(new(StatusCode.InvalidArgument,"Invalid local trust request."));}
        catch(InvalidOperationException){throw new RpcException(new(StatusCode.FailedPrecondition,"Local trust precondition failed."));}
        catch(Exception ex)when(ex is not OutOfMemoryException){throw new RpcException(new(StatusCode.Internal,"Local trust request failed."));}
    }
    public override Task<LocalPeerTrustPageReply> ReadPeerTrust(LocalPeerTrustPageRequest request,ServerCallContext context)
        =>DispatchTrust(context,call=>
        {
            if(request.Limit>16)throw new ArgumentException();
            var page=call.ReadPeerTrust(request.AfterPeerHostId.Length==0?null:Id(request.AfterPeerHostId),request.Limit==0?16:(int)request.Limit);
            var reply=new LocalPeerTrustPageReply {AuthoritativeHostId=page.HostId.ToString("D"),AuthorizationRevision=page.Revision,
                NextAfterPeerHostId=page.NextAfterPeer?.ToString("D")??""};
            foreach(var peer in page.Peers)reply.Peers.Add(new PalworldServerManager.Contracts.Wire.LocalPeerTrustItem {PeerHostId=peer.PeerHostId.ToString("D"),
                State=peer.State switch {"PeerBound"=>LocalPeerTrustState.PeerBound,"Active"=>LocalPeerTrustState.Active,"Revoked"=>LocalPeerTrustState.Revoked,_=>throw new InvalidOperationException()},
                Incarnation=peer.Incarnation,CurrentFingerprint=peer.CurrentFingerprint??"",PendingFingerprint=peer.PendingFingerprint??"",RecoveryRequired=peer.RecoveryRequired});
            if(reply.CalculateSize()>MaximumMessageBytes)throw new InvalidOperationException();return reply;
        });
    public override Task<LocalRevokePeerReply> RevokePeerTrust(LocalRevokePeerRequest request,ServerCallContext context)
        =>DispatchTrust(context,call=>
        {
            if(request.ExpectedAuthorizationRevision<0||request.ExpectedIncarnation<=0)throw new ArgumentException();
            var result=call.RevokePeer(request.ExpectedAuthorizationRevision,Id(request.PeerHostId),request.ExpectedIncarnation);
            return new LocalRevokePeerReply {PeerHostId=result.PeerHostId.ToString("D"),AuthorizationRevision=result.Revision,
                Incarnation=result.Incarnation,Changed=result.Changed};
        });
}
