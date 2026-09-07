using System.Security.Authentication;
using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence;

public sealed record PeerCredentialObservation(PeerTrustRecord Trust, bool Promoted);

public sealed partial class PeerTrustRepository
{
    private static AuthenticationException RotationRefused() => new("Peer credential rotation refused.");
    private static void ValidateRotationMetadata(PeerTrustRecord trust)
    {
        if (trust.CurrentFingerprint is null || trust.PendingRotationId == Guid.Empty ||
            (trust.PendingFingerprint is null && (trust.PendingRotationExpiresUtc is not null || trust.PendingReconfirmationRequired)) ||
            (trust.PendingFingerprint is not null && (trust.PendingRotationId is null || trust.PendingRotationExpiresUtc is null ||
                trust.PendingFingerprint == trust.CurrentFingerprint)))
            throw new InvalidDataException("Incomplete peer rotation metadata.");
    }
    private PeerTrustRecord RequireObservedActivePeer(SqliteConnection c, SqliteTransaction tx, Guid peer, string actualFingerprint)
    {
        if (peer == hostId) throw RotationRefused(); RequireHost(c, tx);
        var trust = Read(c, tx, peer);
        if (trust is null || trust.State != "Active" || trust.RecoveryRequired ||
            (trust.CurrentFingerprint != actualFingerprint && trust.PendingFingerprint != actualFingerprint)) throw RotationRefused();
        ValidateRotationMetadata(trust); return trust;
    }
    // Actual completed mutual-TLS evidence supplied by the trusted Host, never a claimed status
    // or message fingerprint. A previously verified Pending pin remains live-valid after lapse.
    public PeerCredentialObservation ObserveActivePeerCredential(Guid peer, string actualFingerprint)
        => ObserveActivePeerCredentialCore(peer, actualFingerprint, null, CancellationToken.None);
    // Original negotiated connection evidence. Validate it in the SAME transaction that can
    // promote Pending or record lapse; a separate read followed by observation has a race.
    public PeerCredentialObservation ObserveActivePeerCredential(PeerGrantMutationActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.HostId != hostId || actor.PeerHostId == hostId || actor.Incarnation <= 0) throw RotationRefused();
        Fingerprint(actor.LocalFingerprint);
        return ObserveActivePeerCredentialCore(actor.PeerHostId, actor.PeerFingerprint, actor, ct);
    }
    private void RequireObservationConnection(SqliteConnection c, SqliteTransaction tx, PeerGrantMutationActor actor)
    {
        if (RequireHost(c, tx) != actor.LocalFingerprint ||
            PeerRelationshipIncarnation.Read(c, tx, actor.PeerHostId) != actor.Incarnation) throw RotationRefused();
    }
    private long ObservationRevision(SqliteConnection c, SqliteTransaction tx)
    {
        using var command = Command(c, tx, "SELECT Revision FROM AuthorizationRevision WHERE Id=1 AND typeof(Revision)='integer' AND Revision>=0;");
        return command.ExecuteScalar() is long revision ? revision : throw new InvalidDataException("Permission revision unavailable.");
    }
    private PeerCredentialObservation ObserveActivePeerCredentialCore(Guid peer, string actualFingerprint,
        PeerGrantMutationActor? connection, CancellationToken ct)
    {
        Id(peer); Fingerprint(actualFingerprint);
        ct.ThrowIfCancellationRequested();
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        if (connection is not null) RequireObservationConnection(c, tx, connection);
        var revision = connection is null ? 0 : ObservationRevision(c, tx);
        var trust = RequireObservedActivePeer(c, tx, peer, actualFingerprint); var now = time.GetUtcNow();
        Guid? audit; Guid? history = null; string kind; PeerTrustRecord expected;
        var promoted = trust.CurrentFingerprint != actualFingerprint;
        if (trust.CurrentFingerprint == actualFingerprint)
        {
            var lapsed = MarkRotationLapsed(c, tx, trust, now, out audit);
            expected = lapsed ? trust with { PendingReconfirmationRequired = true } : trust;
            kind = "PeerRotationReconfirmationRequired";
        }
        else
        {
            history = Guid.NewGuid();
            Execute(c, tx, """
            INSERT INTO TrustedManagerCredentialHistory (CredentialHistoryId,PeerHostId,PriorPublicKeyFingerprint,RotatedUtc)
                VALUES ($id,$peer,$old,$now);
            UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint=PendingTrustedPublicKeyFingerprint,
                PendingTrustedPublicKeyFingerprint=NULL,PendingRotationExpiresUtc=NULL,PendingReconfirmationRequired=0
                WHERE PeerHostId=$peer;
            """, ("$id", Id(history.Value)), ("$peer", Id(peer)), ("$old", trust.CurrentFingerprint), ("$now", Stamp(now)));
            kind = "PeerCredentialPromoted"; audit = Audit(c, tx, peer, kind, now);
            expected = trust with { CurrentFingerprint = actualFingerprint, PendingFingerprint = null,
                PendingRotationExpiresUtc = null, PendingReconfirmationRequired = false };
        }
        var after = Read(c, tx, peer)!;
        if (connection is not null)
        {
            RequireObservationConnection(c, tx, connection);
            if (ObservationRevision(c, tx) != checked(revision + (audit is null ? 0 : 1)))
                throw new StaleAuthorizationRevisionException();
            if (after != expected) throw new InvalidOperationException("Observed peer credential state changed before commit.");
            if (audit is {} auditId)
            {
                var system = kind == "PeerRotationReconfirmationRequired";
                using var check = Command(c, tx, """
                    SELECT COUNT(*) FROM AuditEvents WHERE AuditEventId=$id AND OccurredUtc=$now AND EventKind=$kind
                        AND ActorKind IS $actor AND ActorPeerHostId IS $peer AND ActorLocalPrincipalId IS NULL
                        AND AffectedHostId=$host AND AffectedServerProfileId IS NULL AND IsOfflineRecovery=0 AND Summary=$summary;
                    """, ("$id", Id(auditId)), ("$now", Stamp(now)), ("$kind", kind), ("$actor", system ? null : "RemoteManager"),
                    ("$peer", system ? null : Id(peer)), ("$host", Id(hostId)), ("$summary", $"{kind}: peer {Id(peer)}."));
                if (Convert.ToInt32(check.ExecuteScalar()) != 1) throw new InvalidOperationException("Peer observation audit changed before commit.");
            }
            if (history is {} historyId)
            {
                using var check = Command(c, tx, """
                    SELECT COUNT(*) FROM TrustedManagerCredentialHistory WHERE CredentialHistoryId=$id AND PeerHostId=$peer
                        AND PriorPublicKeyFingerprint=$old AND RotatedUtc=$now;
                    """, ("$id", Id(historyId)), ("$peer", Id(peer)), ("$old", trust.CurrentFingerprint), ("$now", Stamp(now)));
                if (Convert.ToInt32(check.ExecuteScalar()) != 1) throw new InvalidOperationException("Peer observation history changed before commit.");
            }
        }
        ct.ThrowIfCancellationRequested(); tx.Commit(); return new(after, promoted);
    }
    // Only after the remote Host has confirmed durable receipt for this exact RotationId on
    // the authenticated connection. This primitive does not itself send/receive that RPC.
    public bool ConfirmPeerRotationReceipt(Guid peer, string actualFingerprint, Guid rotationId, string actualLocalFingerprint)
    {
        Id(peer); Id(rotationId); Fingerprint(actualFingerprint);
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        if (RequireHost(c, tx) != actualLocalFingerprint) throw RotationRefused();
        var trust = RequireObservedActivePeer(c, tx, peer, actualFingerprint);
        if (trust.CurrentFingerprint != actualFingerprint || trust.PendingFingerprint is not null) throw RotationRefused();
        if (trust.PendingRotationId is null) return false; // Already cleared: replay has no effect.
        if (trust.PendingRotationId != rotationId) throw RotationRefused();
        Execute(c, tx, "UPDATE TrustedManagers SET PendingRotationId=NULL WHERE PeerHostId=$peer;", ("$peer", Id(peer)));
        Audit(c, tx, peer, "PeerRotationReceiptConfirmed", time.GetUtcNow()); tx.Commit(); return true;
    }
    private bool MarkRotationLapsed(SqliteConnection c, SqliteTransaction tx, PeerTrustRecord trust, DateTimeOffset now)
        => MarkRotationLapsed(c, tx, trust, now, out _);
    private bool MarkRotationLapsed(SqliteConnection c, SqliteTransaction tx, PeerTrustRecord trust, DateTimeOffset now, out Guid? audit)
    {
        audit = null;
        ValidateRotationMetadata(trust);
        if (trust.PendingFingerprint is null || trust.PendingReconfirmationRequired || trust.PendingRotationExpiresUtc > now) return false;
        Execute(c, tx, "UPDATE TrustedManagers SET PendingReconfirmationRequired=1 WHERE PeerHostId=$peer;", ("$peer", Id(trust.PeerHostId)));
        audit = Audit(c, tx, trust.PeerHostId, "PeerRotationReconfirmationRequired", now); return true;
    }
    private void ExpireRotations(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now)
    {
        var peers = new List<Guid>();
        using (var command = Command(c, tx, "SELECT PeerHostId FROM TrustedManagers WHERE State='Active' AND PeerRecoveryRequired=0 AND PendingTrustedPublicKeyFingerprint IS NOT NULL;"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) peers.Add(Guid.ParseExact(reader.GetString(0), "D"));
        foreach (var peer in peers) MarkRotationLapsed(c, tx, Read(c, tx, peer)!, now);
    }
}
