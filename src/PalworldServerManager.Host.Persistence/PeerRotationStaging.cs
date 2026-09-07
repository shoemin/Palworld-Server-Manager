namespace PalworldServerManager.Host.Persistence;

public enum PeerRotationStagingDisposition { Staged = 1, AlreadyStaged = 2, ReconfirmationRequired = 3, PromotionReceiptPending = 4 }
public sealed record PeerRotationStagingResult(PeerRotationStagingDisposition Disposition, Guid RetainedRotationId, DateTimeOffset? ExpiresUtc);

public sealed partial class PeerTrustRepository
{
    private static readonly TimeSpan RotationStagingLifetime = TimeSpan.FromMinutes(30);

    // Proposal content is not proof. Actual fingerprint is obtained independently from completed
    // mutual TLS; the wire adapter must also match the negotiated peer UUID to proposal.HostId.
    public PeerRotationStagingResult StagePeerRotation(HostRotationProposal proposal, string actualFingerprint, string actualLocalFingerprint)
    {
        ArgumentNullException.ThrowIfNull(proposal); Id(proposal.HostId); Id(proposal.RotationId);
        Fingerprint(actualFingerprint); Fingerprint(proposal.OldFingerprint); Fingerprint(proposal.NewFingerprint);
        if (proposal.Sequence <= 0 || proposal.OldFingerprint != actualFingerprint || proposal.NewFingerprint == actualFingerprint) throw RotationRefused();
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        if (RequireHost(c, tx) != actualLocalFingerprint) throw RotationRefused();
        var trust = RequireObservedActivePeer(c, tx, proposal.HostId, actualFingerprint);
        if (trust.CurrentFingerprint != actualFingerprint) throw RotationRefused();
        var known = false;
        using (var command = Command(c, tx, "SELECT ProposalSequence,OldFingerprint,NewFingerprint FROM PeerRotationProposals WHERE PeerHostId=$peer AND RotationId=$id;",
            ("$peer", Id(proposal.HostId)), ("$id", Id(proposal.RotationId))))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                known = true;
                if (reader.GetInt64(0) != proposal.Sequence || reader.GetString(1) != proposal.OldFingerprint || reader.GetString(2) != proposal.NewFingerprint)
                    throw RotationRefused();
            }
        }
        if (trust.PendingFingerprint is not null)
        {
            using var prior = Command(c, tx, "SELECT COUNT(*) FROM PeerRotationProposals WHERE PeerHostId=$peer AND RotationId=$id AND OldFingerprint=$old AND NewFingerprint=$new;",
                ("$peer", Id(proposal.HostId)), ("$id", Id(trust.PendingRotationId!.Value)), ("$old", trust.CurrentFingerprint), ("$new", trust.PendingFingerprint));
            if (Convert.ToInt64(prior.ExecuteScalar()) != 1) throw new InvalidDataException("Pending staging has no verified proposal ordering.");
        }
        var now = time.GetUtcNow(); MarkRotationLapsed(c, tx, trust, now);
        if (trust.PendingFingerprint is not null && (trust.PendingReconfirmationRequired || trust.PendingRotationExpiresUtc <= now))
        {
            tx.Commit(); return new(PeerRotationStagingDisposition.ReconfirmationRequired, trust.PendingRotationId!.Value, trust.PendingRotationExpiresUtc);
        }
        if (trust.PendingFingerprint is null && trust.PendingRotationId is { } receipt)
            return new(PeerRotationStagingDisposition.PromotionReceiptPending, receipt, null);
        if (known)
        {
            if (trust.PendingRotationId != proposal.RotationId || trust.PendingFingerprint != proposal.NewFingerprint) throw RotationRefused();
            return new(PeerRotationStagingDisposition.AlreadyStaged, proposal.RotationId, trust.PendingRotationExpiresUtc);
        }
        using (var latest = Command(c, tx, "SELECT COALESCE(MAX(ProposalSequence),0) FROM PeerRotationProposals WHERE PeerHostId=$peer AND OldFingerprint=$old;",
            ("$peer", Id(proposal.HostId)), ("$old", proposal.OldFingerprint)))
            if (proposal.Sequence <= Convert.ToInt64(latest.ExecuteScalar())) throw RotationRefused();
        var expires = now + RotationStagingLifetime;
        Execute(c, tx, """
            INSERT INTO PeerRotationProposals (PeerHostId,RotationId,ProposalSequence,OldFingerprint,NewFingerprint,AcceptedUtc,OriginalExpiresUtc)
                VALUES ($peer,$id,$sequence,$old,$new,$now,$expires);
            UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint=$new,PendingRotationId=$id,
                PendingRotationExpiresUtc=$expires,PendingReconfirmationRequired=0 WHERE PeerHostId=$peer;
            """, ("$peer", Id(proposal.HostId)), ("$id", Id(proposal.RotationId)), ("$sequence", proposal.Sequence),
            ("$old", proposal.OldFingerprint), ("$new", proposal.NewFingerprint), ("$now", Stamp(now)), ("$expires", Stamp(expires)));
        Audit(c, tx, proposal.HostId, "PeerRotationStaged", now); tx.Commit();
        return new(PeerRotationStagingDisposition.Staged, proposal.RotationId, expires);
    }
}
