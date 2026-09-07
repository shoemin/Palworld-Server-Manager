using Grpc.Core;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

public sealed partial class LocalSecurityRpcService
{
    public override Task<LocalActivatePeerReply> ActivatePeer(LocalActivatePeerRequest request, ServerCallContext context)
        => Dispatch(context, false, false, async session =>
        {
            session.Protocol!.Require(FeatureCapability.LocalOwnerPeerActivation);
            var generation = runtime.Pairing ?? throw new InvalidOperationException("Local activation is not configured.");
            if (request.PeerPort is 0 or > 65535) throw new ArgumentException();
            var peer = Id(request.PeerHostId); var address = HostReachableAddress.Parse(request.ReachableHost, (int)request.PeerPort);
            PeerActivationDisposition result;
            try { result = await generation.ActivateForOwnerAsync(session.Authentication, peer, address, context.CancellationToken).ConfigureAwait(false); }
            catch (RpcException) when (context.CancellationToken.IsCancellationRequested) { throw new OperationCanceledException(context.CancellationToken); }
            catch (RpcException) { throw new RpcException(new(StatusCode.Unavailable, "Peer activation did not complete.")); }
            return new LocalActivatePeerReply { Result = result switch {
                PeerActivationDisposition.Activated => PeerActivationResult.Activated,
                PeerActivationDisposition.AlreadyActive => PeerActivationResult.AlreadyActive,
                _ => throw new InvalidOperationException("Unknown activation disposition.") } };
        });
}
