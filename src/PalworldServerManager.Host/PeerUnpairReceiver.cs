using System.Security.Authentication;
using Microsoft.AspNetCore.Http;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

// One fixed intrinsic security action. No callback or request-controlled auth bypass.
internal sealed class PeerUnpairReceiver(PeerSecurityRpcRuntime runtime,GrantPolicyRepository repository)
{
    internal async Task<PeerUnpairReply> Receive(HttpContext context,PeerUnpairNotice request,CancellationToken ct)
    {
        PermissionDispatchChannel.Require(context);
        var connection=context.Features.Get<PeerSecurityRpcConnection>();
        if(connection is null||!connection.BelongsTo(runtime))throw new AuthenticationException("Peer connection refused.");
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(ct,context.RequestAborted);
        return await connection.Invoke(session=>
        {
            if(session.Protocol is null)throw new InvalidOperationException("Negotiate this connection first.");
            session.Protocol.Require(FeatureCapability.PeerUnpair);
            if(PeerUnpairWire.ReceivingHost(request)!=runtime.HostId)throw new AuthenticationException("Unpair recipient refused.");
            var proof=new PeerGrantMutationActor(runtime.HostId,session.PeerId,session.PeerFingerprint,session.LocalFingerprint,session.PeerIncarnation);
            // Canonical initial Active proof or its narrow committed duplicate receipt.
            // The generic current-trust authentication path is deliberately unchanged.
            var result=repository.ReceiveAuthenticatedPeerUnpair(proof,cancellation.Token);
            return new PeerUnpairReply{ReceivingHostId=runtime.HostId.ToString("D"),UnpairedHostId=session.PeerId.ToString("D"),
                Result=result.Changed?PeerUnpairResult.Recorded:PeerUnpairResult.AlreadyRecorded};
        },cancellation.Token).ConfigureAwait(false);
    }
}
