using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed partial class GrantPolicyRepository
{
    // Only original authenticated connection evidence, NEVER request-body identity. A
    // peer may surrender only itself. This is neither an admin action nor handshake proof.
    public PeerTrustRevocationResult ReceiveAuthenticatedPeerUnpair(PeerGrantMutationActor actor,CancellationToken ct=default)
    {
        var writer=PeerWriter(actor);
        return RevokePeerTrust(writer,null,actor.PeerHostId,actor.Incarnation,ct,actor);
    }

    private bool TryReceivedUnpair(SqliteConnection c,SqliteTransaction tx,PeerGrantMutationActor actor,out long incarnation)
    {
        if(actor.HostId!=hostId||actor.PeerHostId==Guid.Empty||actor.PeerHostId==hostId||actor.Incarnation<=0||
            !HostTrustPlanning.Fingerprint(actor.PeerFingerprint)||!HostTrustPlanning.Fingerprint(actor.LocalFingerprint)||
            RevocationCredential(c,tx).Fingerprint!=actor.LocalFingerprint)
            throw new AuthenticationException("Current unpair connection proof required.");
        using var cmd=Command(c,tx,"""
            SELECT r.RevokedIncarnation FROM PeerUnpairReceipts r
                JOIN TrustedManagers t ON t.PeerHostId=r.PeerHostId
                JOIN PeerRelationshipIncarnations i ON i.PeerHostId=t.PeerHostId AND i.Incarnation=r.RevokedIncarnation
            WHERE r.PeerHostId=$peer AND r.SourceIncarnation=$source AND r.PeerFingerprint=$remote AND r.LocalFingerprint=$local
                AND t.State='Revoked' AND t.CurrentTrustedPublicKeyFingerprint IS NULL
                AND t.PendingTrustedPublicKeyFingerprint IS NULL AND t.PendingRotationId IS NULL AND t.PendingRotationExpiresUtc IS NULL
                AND t.PendingReconfirmationRequired=0 AND t.PeerRecoveryRequired=0 AND t.RevokedUtc=r.ReceivedUtc;
            """,("$peer",Id(actor.PeerHostId)),("$source",actor.Incarnation),("$remote",actor.PeerFingerprint),("$local",actor.LocalFingerprint));
        var value=cmd.ExecuteScalar();incarnation=value is long found?found:0;return incarnation>actor.Incarnation;
    }

    private static Action WriteReceivedUnpair(SqliteConnection c,SqliteTransaction tx,PeerGrantMutationActor actor,long incarnation,string stamp)
    {
        var args=new (string,object?)[]{("$peer",Id(actor.PeerHostId)),("$source",actor.Incarnation),("$revoked",incarnation),
            ("$remote",actor.PeerFingerprint),("$local",actor.LocalFingerprint),("$now",stamp)};
        Execute(c,tx,"""
            INSERT INTO PeerUnpairReceipts (PeerHostId,SourceIncarnation,RevokedIncarnation,PeerFingerprint,LocalFingerprint,ReceivedUtc)
            VALUES ($peer,$source,$revoked,$remote,$local,$now);
            """,args);
        return ()=>
        {
            using var cmd=Command(c,tx,"""
                SELECT COUNT(*) FROM PeerUnpairReceipts WHERE PeerHostId=$peer AND SourceIncarnation=$source
                    AND RevokedIncarnation=$revoked AND PeerFingerprint=$remote AND LocalFingerprint=$local AND ReceivedUtc=$now;
                """,args);
            if(Convert.ToInt32(cmd.ExecuteScalar())!=1)throw new InvalidOperationException("Received unpair evidence changed before commit.");
        };
    }
}
