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
    {
        Id(peer); Fingerprint(actualFingerprint);
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        var trust = RequireObservedActivePeer(c, tx, peer, actualFingerprint); var now = time.GetUtcNow();
        if (trust.CurrentFingerprint == actualFingerprint)
        {
            MarkRotationLapsed(c, tx, trust, now); var current = Read(c, tx, peer)!; tx.Commit(); return new(current, false);
        }
        Execute(c, tx, """
            INSERT INTO TrustedManagerCredentialHistory (CredentialHistoryId,PeerHostId,PriorPublicKeyFingerprint,RotatedUtc)
                VALUES ($id,$peer,$old,$now);
            UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint=PendingTrustedPublicKeyFingerprint,
                PendingTrustedPublicKeyFingerprint=NULL,PendingRotationExpiresUtc=NULL,PendingReconfirmationRequired=0
                WHERE PeerHostId=$peer;
            """, ("$id", Id(Guid.NewGuid())), ("$peer", Id(peer)), ("$old", trust.CurrentFingerprint), ("$now", Stamp(now)));
        Audit(c, tx, peer, "PeerCredentialPromoted", now);
        var promoted = Read(c, tx, peer)!; tx.Commit(); return new(promoted, true);
    }
    // Only after the remote Host has confirmed durable receipt for this exact RotationId on
    // the authenticated connection. This primitive does not itself send/receive that RPC.
    public bool ConfirmPeerRotationReceipt(Guid peer, string actualFingerprint, Guid rotationId)
    {
        Id(peer); Id(rotationId); Fingerprint(actualFingerprint);
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        var trust = RequireObservedActivePeer(c, tx, peer, actualFingerprint);
        if (trust.CurrentFingerprint != actualFingerprint || trust.PendingFingerprint is not null) throw RotationRefused();
        if (trust.PendingRotationId is null) return false; // Already cleared: replay has no effect.
        if (trust.PendingRotationId != rotationId) throw RotationRefused();
        Execute(c, tx, "UPDATE TrustedManagers SET PendingRotationId=NULL WHERE PeerHostId=$peer;", ("$peer", Id(peer)));
        Audit(c, tx, peer, "PeerRotationReceiptConfirmed", time.GetUtcNow()); tx.Commit(); return true;
    }
    private bool MarkRotationLapsed(SqliteConnection c, SqliteTransaction tx, PeerTrustRecord trust, DateTimeOffset now)
    {
        ValidateRotationMetadata(trust);
        if (trust.PendingFingerprint is null || trust.PendingReconfirmationRequired || trust.PendingRotationExpiresUtc > now) return false;
        Execute(c, tx, "UPDATE TrustedManagers SET PendingReconfirmationRequired=1 WHERE PeerHostId=$peer;", ("$peer", Id(trust.PeerHostId)));
        Audit(c, tx, trust.PeerHostId, "PeerRotationReconfirmationRequired", now); return true;
    }
    private void ExpireRotations(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now)
    {
        var peers = new List<Guid>();
        using (var command = Command(c, tx, "SELECT PeerHostId FROM TrustedManagers WHERE State='Active' AND PeerRecoveryRequired=0 AND PendingTrustedPublicKeyFingerprint IS NOT NULL;"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) peers.Add(Guid.ParseExact(reader.GetString(0), "D"));
        foreach (var peer in peers) MarkRotationLapsed(c, tx, Read(c, tx, peer)!, now);
    }
}
