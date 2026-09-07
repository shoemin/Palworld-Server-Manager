using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

public sealed partial class LocalSecurityRpcService
{
    private HostNetworkGeneration Pairing(LocalSecurityRpcConnection session)
    {
        session.Protocol!.Require(FeatureCapability.LocalOwnerPairing);
        return runtime.Pairing ?? throw new InvalidOperationException("Local pairing is not configured.");
    }
    public override Task<LocalPairingInvitation> CreatePairingInvitation(LocalEmpty request, ServerCallContext context)
        => Dispatch(context, false, false, session => Pairing(session).CreateInvitationForOwnerAsync(session.Authentication, invitation =>
        {
            using (invitation)
            {
                var code = invitation.Code.CopyBytes();
                try
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    return new LocalPairingInvitation { InvitationId = invitation.Id.ToString("D"), ExpiresUtc = invitation.ExpiresUtc.ToString("O"), Code = ByteString.CopyFrom(code) };
                }
                finally { CryptographicOperations.ZeroMemory(code); }
            }
        }, context.CancellationToken));
    public override Task<LocalEmpty> CancelPairingInvitation(LocalPairingInvitationRequest request, ServerCallContext context)
        => Dispatch(context, false, false, async session =>
        {
            await Pairing(session).CancelInvitationForOwnerAsync(session.Authentication, Id(request.InvitationId), context.CancellationToken).ConfigureAwait(false);
            return new LocalEmpty();
        });
    public override Task<LocalPairingDiscoveryReply> DiscoverPairingHosts(LocalPairingDiscoveryRequest request, ServerCallContext context)
        => Dispatch(context, false, false, async session =>
        {
            var generation = Pairing(session);
            if (request.Offset > HostDiscoveryDirectory.MaximumEntries || request.Limit > 32) throw new ArgumentException();
            var hints = await generation.DiscoverForOwnerAsync(session.Authentication, context.CancellationToken).ConfigureAwait(false);
            var offset = (int)request.Offset; var limit = request.Limit == 0 ? 32 : (int)request.Limit;
            var reply = new LocalPairingDiscoveryReply();
            foreach (var hint in hints.Skip(offset).Take(limit))
                reply.Hosts.Add(new LocalDiscoveredPairingHost { ClaimedHostId = hint.Advertisement.ClaimedHostId.ToString("D"),
                    ReachableHost = hint.PairingAddress.Host, PeerPort = (uint)hint.Advertisement.PeerPort,
                    PairingPort = (uint)hint.Advertisement.PairingPort, AdvertisedProtocol = new() {
                        Major = hint.Advertisement.ProtocolMajor, Minor = hint.Advertisement.ProtocolMinor } });
            if (offset + reply.Hosts.Count < hints.Count) reply.NextOffset = (uint)(offset + reply.Hosts.Count);
            if (reply.CalculateSize() > MaximumMessageBytes) throw new InvalidOperationException();
            return reply;
        });
    public override Task<LocalPairHostReply> PairHost(LocalPairHostRequest request, ServerCallContext context)
        => Dispatch(context, false, false, async session =>
        {
            var generation = Pairing(session);
            if (request.PairingPort is 0 or > 65535 || request.Code.Length != 10 || request.Code.Any(value => value is < (byte)'0' or > (byte)'9')) throw new ArgumentException();
            var address = HostReachableAddress.Parse(request.ReachableHost, (int)request.PairingPort);
            var bytes = request.Code.ToByteArray(); RedactedSecret code;
            try { code = new(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
            using (code)
            {
                PeerPairingCompletion result;
                try { result = await generation.PairForOwnerAsync(session.Authentication, address, code, context.CancellationToken).ConfigureAwait(false); }
                catch (RpcException) when (context.CancellationToken.IsCancellationRequested) { throw new OperationCanceledException(context.CancellationToken); }
                catch (RpcException) { throw new RpcException(new(StatusCode.Unavailable, "Peer pairing did not complete.")); }
                return new LocalPairHostReply { VerifiedPeerHostId = result.Local.PeerHostId.ToString("D"), LocalResult = PairingResult(result.Local.Disposition),
                    RemoteResult = result.Remote, LocalReplacementId = result.Local.ReplacementId?.ToString("D") ?? "",
                    LocalExpiresUtc = result.Local.ExpiresUtc?.ToString("O") ?? "" };
            }
        });
    private static PeerPairingResult PairingResult(PeerBindingDisposition disposition) => disposition switch
    {
        PeerBindingDisposition.PeerBoundCreated => PeerPairingResult.PeerBound,
        PeerBindingDisposition.ResumePeerBound => PeerPairingResult.Resumed,
        PeerBindingDisposition.ActiveReconfirmed => PeerPairingResult.Reconfirmed,
        PeerBindingDisposition.ReplacementRequired => PeerPairingResult.ReplacementRequired,
        PeerBindingDisposition.RecoveryRequired => PeerPairingResult.RecoveryRequired,
        _ => throw new InvalidOperationException("Unknown pairing disposition.")
    };
}
