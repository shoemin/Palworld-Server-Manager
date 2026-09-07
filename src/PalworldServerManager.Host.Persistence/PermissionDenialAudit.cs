using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed partial class GrantPolicyRepository
{
    // Constructed only by this repository from typed, bounded-value fields. Never a DTO,
    // free-text reason, preset label, exception message or authentication proof.
    private sealed record PermissionAttempt(string Action,Guid Host,Guid? Server,string Detail);
    private static PermissionAttempt GrantAttempt(GrantRequest request)
    {
        var h=request as HostGrantRequest;var s=request as ServerGrantRequest;
        return new(h is not null?"IssueHostGrant":"IssueServerGrant",h?.TargetHostId??s!.Target.AuthoritativeHostId,s?.Target.ServerProfileId,
            $"Grant={Id(request.GrantId)}; Capability={h?.Capability.ToString()??s!.Capability.ToString()}; Grantee={request.Grantee.Kind}:{Id(request.Grantee.Id)}; Source={request.SourceGrantId?.ToString("D")??"RootRequested"}; Delegate={request.Rights.CanDelegate}; Onward={request.Rights.CanDelegateOnwardDelegation}");
    }
    // MUST run before any requested mutation/callback. A denial commits only its audit.
    // Authentication/revision validation precedes this call. Post-write failures must never
    // enter it: they roll back the entire transaction instead of committing partial effects.
    private T AuthorizeOrAudit<T>(SqliteConnection c,SqliteTransaction tx,GrantWriter actor,long revision,
        PermissionAttempt attempt,Func<T> authorize,CancellationToken ct)
    {
        try{return authorize();}
        catch(UnauthorizedAccessException)
        {
            ct.ThrowIfCancellationRequested();
            var id=Guid.NewGuid();var occurred=Stamp(time.GetUtcNow());
            var local=actor.Actual.Kind==ActorKind.LocalPrincipal?Id(actor.Actual.Id):null;
            var peer=actor.Actual.Kind==ActorKind.RemoteManager?Id(actor.Actual.Id):null;
            var summary=$"Action={attempt.Action}; {attempt.Detail}; Outcome=PolicyDenied.";
            var args=new (string,object?)[]{("$id",Id(id)),("$now",occurred),("$kind",actor.Actual.Kind.ToString()),
                ("$local",local),("$peer",peer),("$host",Id(attempt.Host)),("$server",attempt.Server?.ToString("D")),("$summary",summary)};
            Execute(c,tx,"""
                INSERT INTO AuditEvents (AuditEventId,OccurredUtc,EventKind,ActorKind,ActorLocalPrincipalId,ActorPeerHostId,
                    AffectedHostId,AffectedServerProfileId,IsOfflineRecovery,Summary)
                VALUES ($id,$now,'PermissionPolicyDenied',$kind,$local,$peer,$host,$server,0,$summary);
                """,args);
            var after=Read(c,tx);actor.Require(c,tx,after);RequireRevision(revision,after.Revision);
            using var check=Command(c,tx,"""
                SELECT COUNT(*) FROM AuditEvents WHERE AuditEventId=$id AND OccurredUtc=$now
                    AND EventKind='PermissionPolicyDenied' AND ActorKind=$kind AND ActorLocalPrincipalId IS $local
                    AND ActorPeerHostId IS $peer AND AffectedHostId=$host AND AffectedServerProfileId IS $server
                    AND IsOfflineRecovery=0 AND Summary=$summary;
                """,args);
            if(Convert.ToInt32(check.ExecuteScalar())!=1)throw new InvalidOperationException("Permission denial audit changed before commit.");
            ct.ThrowIfCancellationRequested();tx.Commit();throw;
        }
    }
}
