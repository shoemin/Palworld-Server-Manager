using System.Security.Authentication;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Contracts;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

internal enum RotationAcceptanceBlock
{
    PendingPairing = 1, RecoveryRequired = 2, MissingAddress = 3, ContactFailed = 4,
    ReconfirmationRequired = 5, PromotionReceiptPending = 6, InsufficientTime = 7,
    PeerSetChanged = 8, ProposalChanged = 9, EvidenceMismatch = 10
}
internal sealed record RotationAcceptanceBlocker(Guid? PeerHostId, RotationAcceptanceBlock Reason);
internal sealed record RotationAcceptanceAssessment(IReadOnlyList<RotationAcceptanceBlocker> Blockers)
{
    internal bool PeerAcknowledgementsReady => Blockers.Count == 0;
}

// Process-local evidence, never a serializable permission or a persisted readiness flag.
internal sealed class RotationAcceptanceCollection(object scope, RoutineRotationPeerSet snapshot,
    IReadOnlyList<RotationAcceptanceBlocker> blockers, IReadOnlyDictionary<Guid, PeerRotationProposalExchange> exchanges)
{
    internal object Scope { get; } = scope;
    internal RoutineRotationPeerSet Snapshot { get; } = snapshot;
    internal IReadOnlyList<RotationAcceptanceBlocker> InitialBlockers { get; } = blockers;
    internal IReadOnlyDictionary<Guid, PeerRotationProposalExchange> Exchanges { get; } = exchanges;
}

internal sealed class RoutineRotationAcceptanceCollector(PeerSecurityRpcRuntime runtime, IPeerHttpTransportFactory transport)
{
    internal static readonly TimeSpan MinimumRemainingMargin = TimeSpan.FromMinutes(2);
    private readonly object scope = new();
    internal async Task<RotationAcceptanceCollection> CollectAsync(Guid rotationId, IReadOnlyDictionary<Guid, Uri> addresses, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(addresses); ct.ThrowIfCancellationRequested();
        var routes = addresses.ToDictionary(p => p.Key, p => p.Value); // Freeze the Host's routing input for this round.
        var snapshot = runtime.Credentials.ReadRoutineRotationPeerSet(rotationId);
        var blockers = new System.Collections.Concurrent.ConcurrentBag<RotationAcceptanceBlocker>();
        var exchanges = new System.Collections.Concurrent.ConcurrentDictionary<Guid, PeerRotationProposalExchange>();
        await Parallel.ForEachAsync(snapshot.Peers, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (peer, token) =>
        {
            if (peer.State == "PeerBound") { blockers.Add(new(peer.PeerHostId, RotationAcceptanceBlock.PendingPairing)); return; }
            if (peer.RecoveryRequired) { blockers.Add(new(peer.PeerHostId, RotationAcceptanceBlock.RecoveryRequired)); return; }
            if (!routes.TryGetValue(peer.PeerHostId, out var address)) { blockers.Add(new(peer.PeerHostId, RotationAcceptanceBlock.MissingAddress)); return; }
            try
            {
                var reply = await new PeerRotationProposalRpcClient(runtime, transport).StageAsync(peer.PeerHostId, address, rotationId, token).ConfigureAwait(false);
                if (reply.PeerHostId != peer.PeerHostId || reply.ActualPeerFingerprint != peer.CurrentFingerprint || reply.Proposal != snapshot.Proposal)
                    blockers.Add(new(peer.PeerHostId, RotationAcceptanceBlock.EvidenceMismatch));
                else if (reply.Outcome == PeerRotationProposalOutcome.ReconfirmationRequired)
                    blockers.Add(new(peer.PeerHostId, RotationAcceptanceBlock.ReconfirmationRequired));
                else if (reply.Outcome == PeerRotationProposalOutcome.PromotionReceiptPending)
                    blockers.Add(new(peer.PeerHostId, RotationAcceptanceBlock.PromotionReceiptPending));
                else if (reply.Outcome != PeerRotationProposalOutcome.Acknowledged || reply.RetainedRotationId != rotationId)
                    blockers.Add(new(peer.PeerHostId, RotationAcceptanceBlock.EvidenceMismatch));
                else exchanges[peer.PeerHostId] = reply;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { blockers.Add(new(peer.PeerHostId, RotationAcceptanceBlock.ContactFailed)); }
            catch (Exception ex) when (ex is RpcException or AuthenticationException or ArgumentException or InvalidOperationException or ProtocolCompatibilityException or IOException or SqliteException)
            { token.ThrowIfCancellationRequested(); blockers.Add(new(peer.PeerHostId, RotationAcceptanceBlock.ContactFailed)); }
        }).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var collection = new RotationAcceptanceCollection(scope, snapshot, Array.AsReadOnly(blockers.OrderBy(b => b.PeerHostId).ToArray()),
            new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, PeerRotationProposalExchange>(exchanges.ToDictionary(p => p.Key, p => p.Value)));
        // Force a fresh read after all exchanges. A later caller must recheck again;
        // neither this read nor its report is the eventual cutover transaction.
        Recheck(collection); return collection;
    }
    internal RotationAcceptanceAssessment Recheck(RotationAcceptanceCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (!ReferenceEquals(collection.Scope, scope)) throw new InvalidOperationException("Acceptance collection belongs to another Host round owner.");
        var blocked = collection.InitialBlockers.ToList();
        RoutineRotationPeerSet current;
        try { current = runtime.Credentials.ReadRoutineRotationPeerSet(collection.Snapshot.Proposal.RotationId); }
        catch (AuthenticationException) { blocked.Add(new(null, RotationAcceptanceBlock.ProposalChanged)); return new(blocked.AsReadOnly()); }
        if (current.Proposal != collection.Snapshot.Proposal) blocked.Add(new(null, RotationAcceptanceBlock.ProposalChanged));
        if (current.Revision != collection.Snapshot.Revision || !current.Peers.SequenceEqual(collection.Snapshot.Peers))
            blocked.Add(new(null, RotationAcceptanceBlock.PeerSetChanged));
        foreach (var peer in collection.Snapshot.Peers)
        {
            if (!collection.Exchanges.TryGetValue(peer.PeerHostId, out var exchange))
            {
                if (!blocked.Any(b => b.PeerHostId == peer.PeerHostId)) blocked.Add(new(peer.PeerHostId, RotationAcceptanceBlock.ContactFailed));
            }
            else if (exchange.RemainingAcceptance <= MinimumRemainingMargin)
                blocked.Add(new(peer.PeerHostId, RotationAcceptanceBlock.InsufficientTime));
        }
        return new(blocked.AsReadOnly());
    }
}
