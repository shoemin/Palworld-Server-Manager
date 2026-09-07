using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;

namespace PalworldServerManager.Host.Persistence;

public sealed partial class GrantPolicyRepository
{
    // Private controlled fields only. The returned validator belongs to this transaction
    // and must run after ALL batch/enclosing audits, immediately before final cancellation
    // and commit. A later insert trigger can otherwise remove or rewrite an earlier row.
    private static Action WriteSuccessAudit(SqliteConnection c,SqliteTransaction tx,ActorRef actor,
        Guid host,Guid? server,string kind,DateTimeOffset now,string summary)
    {
        var args=new (string,object?)[]{("$id",Id(Guid.NewGuid())),("$now",Stamp(now)),("$kind",kind),
            ("$actorKind",actor.Kind.ToString()),("$local",actor.Kind==ActorKind.LocalPrincipal?Id(actor.Id):null),
            ("$peer",actor.Kind==ActorKind.RemoteManager?Id(actor.Id):null),("$host",Id(host)),
            ("$server",server?.ToString("D")),("$summary",summary)};
        Execute(c,tx,"""
            INSERT INTO AuditEvents (AuditEventId,OccurredUtc,EventKind,ActorKind,ActorLocalPrincipalId,ActorPeerHostId,
                AffectedHostId,AffectedServerProfileId,IsOfflineRecovery,Summary)
            VALUES ($id,$now,$kind,$actorKind,$local,$peer,$host,$server,0,$summary);
            """,args);
        return ()=>
        {
            using var check=Command(c,tx,"""
                SELECT COUNT(*) FROM AuditEvents WHERE AuditEventId=$id AND OccurredUtc=$now AND EventKind=$kind
                    AND ActorKind=$actorKind AND ActorLocalPrincipalId IS $local AND ActorPeerHostId IS $peer
                    AND AffectedHostId=$host AND AffectedServerProfileId IS $server AND IsOfflineRecovery=0 AND Summary=$summary;
                """,args);
            if(Convert.ToInt32(check.ExecuteScalar())!=1)
                throw new InvalidOperationException("Successful permission audit changed before commit.");
        };
    }
}
