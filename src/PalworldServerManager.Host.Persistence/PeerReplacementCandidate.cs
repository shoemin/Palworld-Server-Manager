using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence;

public sealed partial class PeerTrustRepository
{
    private static long CandidateAuthorizationRevision(SqliteConnection c,SqliteTransaction tx)
    {
        using var cmd=Command(c,tx,"SELECT Revision FROM AuthorizationRevision WHERE Id=1;");
        return cmd.ExecuteScalar() is long value&&value>=0?value:throw new InvalidDataException("Authorization revision unavailable.");
    }
    private void RequireCandidateCreated(SqliteConnection c,SqliteTransaction tx,PeerTrustRecord original,string proposed,
        string local,long incarnation,long revision,Guid id,DateTimeOffset now,DateTimeOffset expires,Guid audit)
    {
        if(Read(c,tx,original.PeerHostId)!=original||PeerRelationshipIncarnation.Read(c,tx,original.PeerHostId)!=incarnation||
            RequireHost(c,tx)!=local||CandidateAuthorizationRevision(c,tx)!=revision)
            throw new InvalidOperationException("Verified replacement context changed before commit.");
        using var check=Command(c,tx,"""
            SELECT COUNT(*) FROM PendingCredentialReplacements r JOIN PeerReplacementBindingEvidence e ON e.ReplacementId=r.ReplacementId
            WHERE r.ReplacementId=$id AND r.PeerHostId=$peer AND r.ProposedKeyFingerprint=$fp
                AND r.VerifiedUtc=$now AND r.ExpiresUtc=$expires AND r.CreatedUtc=$now
                AND r.ExpectedTrustState=$state AND r.ExpectedCurrentTrustedPublicKeyFingerprint IS $old
                AND r.ApprovedByOwnerLocalPrincipalId IS NULL AND r.ApprovedUtc IS NULL AND r.InvalidatedUtc IS NULL
                AND e.SourceIncarnation=$inc AND e.LocalFingerprint=$local AND e.VerifiedUtc=$now;
            """,("$id",Id(id)),("$peer",Id(original.PeerHostId)),("$fp",proposed),("$now",Stamp(now)),("$expires",Stamp(expires)),
            ("$state",original.State),("$old",original.CurrentFingerprint),("$inc",incarnation),("$local",local));
        if(Convert.ToInt32(check.ExecuteScalar())!=1)throw new InvalidOperationException("Verified replacement candidate changed before commit.");
        using var auditCheck=Command(c,tx,"""
            SELECT COUNT(*) FROM AuditEvents WHERE AuditEventId=$id AND OccurredUtc=$now AND EventKind='PeerCredentialReplacementPending'
                AND ActorKind='RemoteManager' AND ActorPeerHostId=$peer AND ActorLocalPrincipalId IS NULL
                AND AffectedHostId=$host AND AffectedServerProfileId IS NULL AND IsOfflineRecovery=0 AND Summary=$summary;
            """,("$id",Id(audit)),("$now",Stamp(now)),("$peer",Id(original.PeerHostId)),("$host",Id(hostId)),
            ("$summary",$"PeerCredentialReplacementPending: peer {Id(original.PeerHostId)}."));
        if(Convert.ToInt32(auditCheck.ExecuteScalar())!=1)throw new InvalidOperationException("Verified replacement audit changed before commit.");
    }
}
