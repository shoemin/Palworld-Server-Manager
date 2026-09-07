using System.Net;
using System.Security.Authentication;
using Grpc.Net.Client;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

internal enum PeerRotationProposalOutcome { Acknowledged = 1, ReconfirmationRequired = 2, PromotionReceiptPending = 3 }
// Public acknowledgement evidence with a decreasing conservative bound, not permission to
// cut over. The future global engine must freshly verify its full dynamic peer set.
internal sealed class PeerRotationProposalExchange(PeerRotationProposalOutcome outcome, Guid retainedRotationId,
    long remoteRemainingMilliseconds, TimeProvider clock, long started, Guid peerHostId = default,
    string? actualPeerFingerprint = null, HostRotationProposal? proposal = null)
{
    private long greatestElapsedTicks;
    internal PeerRotationProposalOutcome Outcome { get; } = outcome;
    internal Guid RetainedRotationId { get; } = retainedRotationId;
    internal Guid PeerHostId { get; } = peerHostId;
    internal string? ActualPeerFingerprint { get; } = actualPeerFingerprint;
    internal HostRotationProposal? Proposal { get; } = proposal;
    internal TimeSpan RemainingAcceptance
    {
        get
        {
            var elapsed = clock.GetElapsedTime(started);
            // A clock anomaly can only shorten this process-local bound, never revive it.
            var observed = elapsed < TimeSpan.Zero ? long.MaxValue : elapsed.Ticks;
            var prior = Volatile.Read(ref greatestElapsedTicks);
            while (observed > prior)
            {
                var actual = Interlocked.CompareExchange(ref greatestElapsedTicks, observed, prior);
                if (actual == prior) break;
                prior = actual;
            }
            var remaining = TimeSpan.FromMilliseconds(remoteRemainingMilliseconds).Ticks - Volatile.Read(ref greatestElapsedTicks);
            return TimeSpan.FromTicks(Math.Max(0, remaining));
        }
    }
}

internal sealed class PeerRotationProposalRpcClient(PeerSecurityRpcRuntime runtime, IPeerHttpTransportFactory transport)
{
    internal async Task<PeerRotationProposalExchange> StageAsync(Guid peer, Uri address, Guid rotationId, CancellationToken ct = default)
    {
        if (peer == Guid.Empty || peer == runtime.HostId || !address.IsAbsoluteUri || address.Scheme != "https" ||
            address.UserInfo.Length != 0 || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException("A reachable peer HTTPS address is required.");
        ct.ThrowIfCancellationRequested(); var started = runtime.Clock.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var proposal = runtime.Credentials.ReadRoutineRotationProposal(rotationId); var request = PeerRotationProposalWire.Wire(proposal);
        using var connection = transport.Create(
            pin => runtime.Authentication.AdmitHandshake(peer, pin, PeerTrafficPurpose.TrustMaintenance),
            actual => runtime.Authentication.Authenticate(peer, actual.PeerFingerprint, PeerTrafficPurpose.TrustMaintenance));
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = connection.Handler, HttpVersion = HttpVersion.Version20, HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            MaxReceiveMessageSize = PeerSecurityRpcService.MaximumMessageBytes, MaxSendMessageSize = PeerSecurityRpcService.MaximumMessageBytes
        });
        var client = new PeerSecurityProtocol.PeerSecurityProtocolClient(channel); var hello = PeerSecurityRpcRuntime.Hello(runtime.HostId);
        var negotiated = await client.NegotiateAsync(hello, cancellationToken: deadline.Token).ResponseAsync.ConfigureAwait(false);
        if (negotiated.Host is null || PeerSecurityRpcService.Id(negotiated.Host.HostId) != peer) throw new AuthenticationException("Peer identity refused.");
        NegotiatedProtocol.Negotiate(hello.Handshake, negotiated.Handshake).Require(FeatureCapability.PeerRotationProposal);
        var actual = connection.Identity;
        if (actual.LocalFingerprint != proposal.OldFingerprint || runtime.Credentials.ReadRoutineRotationProposal(rotationId) != proposal)
            throw new AuthenticationException("Current rotation proposal changed.");
        var reply = await client.StageRotationAsync(request, cancellationToken: deadline.Token).ResponseAsync.ConfigureAwait(false);
        deadline.Token.ThrowIfCancellationRequested();
        if (reply.Request is null || !reply.Request.Equals(request) || reply.RemainingAcceptanceMilliseconds is < 0 or > PeerRotationProposalWire.MaximumAcceptanceMilliseconds)
            throw new AuthenticationException("Peer staging acknowledgement refused.");
        var retained = PeerSecurityRpcService.Id(reply.RetainedRotationId);
        var outcome = reply.State switch
        {
            PeerRotationStagingState.Staged or PeerRotationStagingState.AlreadyStaged when retained == rotationId => PeerRotationProposalOutcome.Acknowledged,
            PeerRotationStagingState.ReconfirmationRequired when reply.RemainingAcceptanceMilliseconds == 0 => PeerRotationProposalOutcome.ReconfirmationRequired,
            PeerRotationStagingState.PromotionReceiptPending when reply.RemainingAcceptanceMilliseconds == 0 => PeerRotationProposalOutcome.PromotionReceiptPending,
            _ => throw new AuthenticationException("Peer staging acknowledgement refused.")
        };
        if (outcome == PeerRotationProposalOutcome.Acknowledged)
            runtime.Credentials.RecordRoutineRotationPeerAcknowledgement(proposal, peer, actual.LocalFingerprint, actual.PeerFingerprint);
        return new(outcome, retained, reply.RemainingAcceptanceMilliseconds, runtime.Clock, started, peer, actual.PeerFingerprint, proposal);
    }
}
