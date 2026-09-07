using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

internal static class PeerRotationProposalWire
{
    internal const long MaximumAcceptanceMilliseconds = 30 * 60 * 1000;
    internal static HostRotationProposal Durable(PeerRotationProposalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); PeerSecurityRpcService.Id(request.RequestId);
        return new(PeerSecurityRpcService.Id(request.HostId), PeerSecurityRpcService.Id(request.RotationId), request.Sequence, request.OldFingerprint, request.NewFingerprint);
    }
    internal static PeerRotationProposalRequest Wire(HostRotationProposal proposal) => new()
    { RequestId = Guid.NewGuid().ToString("D"), HostId = proposal.HostId.ToString("D"), RotationId = proposal.RotationId.ToString("D"), Sequence = proposal.Sequence, OldFingerprint = proposal.OldFingerprint, NewFingerprint = proposal.NewFingerprint };
    internal static PeerRotationProposalReply Reply(PeerRotationProposalRequest request, PeerRotationStagingResult result, DateTimeOffset now) => new()
    {
        Request = request.Clone(), RetainedRotationId = result.RetainedRotationId.ToString("D"),
        State = result.Disposition switch
        {
            PeerRotationStagingDisposition.Staged => PeerRotationStagingState.Staged,
            PeerRotationStagingDisposition.AlreadyStaged => PeerRotationStagingState.AlreadyStaged,
            PeerRotationStagingDisposition.ReconfirmationRequired => PeerRotationStagingState.ReconfirmationRequired,
            PeerRotationStagingDisposition.PromotionReceiptPending => PeerRotationStagingState.PromotionReceiptPending,
            _ => throw new ArgumentException("Unknown staging state.")
        },
        RemainingAcceptanceMilliseconds = result.Disposition is PeerRotationStagingDisposition.Staged or PeerRotationStagingDisposition.AlreadyStaged && result.ExpiresUtc is { } expires
            ? (long)Math.Clamp(Math.Floor((expires - now).TotalMilliseconds), 0, MaximumAcceptanceMilliseconds) : 0
    };
}
