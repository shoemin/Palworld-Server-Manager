using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static class PermissionDenialAuditTests
{
    private static readonly DelegationRights Use=new(false,false),Onward=new(true,true);
    private static void Check(bool value){if(!value)throw new Exception("Permission denial audit assertion failed.");}
    private static void Reject<T>(Action action)where T:Exception
    {try{action();}catch(T){return;}throw new Exception("Expected denial audit refusal: "+typeof(T).Name);}
    private sealed class Rig:IDisposable
    {
        internal readonly PeerTrustTests.Fixture F=new();
        internal readonly Guid User=Guid.NewGuid();
        internal readonly ServerRef Target;
        internal GrantPolicyRepository Repo=>new(F.Database,F.HostId,F.Time);
        internal LocalPrincipalMutationActor Owner=>new(F.HostId,F.OwnerId,"native-owner","fixture-public");
        internal LocalPrincipalMutationActor Local=>new(F.HostId,User,"private-native-user","private-user-key");
        internal ActorRef Grantee=>ActorRef.RemoteManager(F.PeerId);
        internal long Revision=>Repo.Read().Revision;
        internal long Denials=>HostDatabase.QueryScalarLong(F.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PermissionPolicyDenied';");
        internal PeerGrantMutationActor Peer=>new(F.HostId,F.PeerId,new('B',64),new('A',64),HostDatabase.QueryScalarLong(F.Writer,$"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{F.PeerId:D}';"));
        internal Rig()
        {
            Target=new(F.HostId,Guid.NewGuid());
            F.Execute($"INSERT INTO LocalPrincipals (LocalPrincipalId,OsPrincipalRef,PublicVerificationKey,IsOwner,State,CreatedUtc) VALUES ('{User:D}','private-native-user','private-user-key',0,'Active','{F.Time.Now:O}');");
            F.Execute($"INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('{F.PeerId:D}','Active','{new string('B',64)}','{F.Time.Now:O}');");
        }
        internal void DenyLocal(GrantPolicyRepository? repo=null,long? revision=null,CancellationToken ct=default)
            =>(repo??Repo).IssueHost(Local,revision??Revision,Guid.NewGuid(),Grantee,HostCapability.CreateServer,F.HostId,Use,null,ct);
        internal void DenyPeer(GrantPolicyRepository? repo=null)
            =>(repo??Repo).IssueRemoteServer(Peer,Revision,Guid.NewGuid(),Grantee,ServerCapability.ViewServer,Target,Use,null);
        internal RolePreset Preset()=>new("private-preset-label",[new(Guid.NewGuid(),Grantee,HostCapability.CreateServer,F.HostId,Use)],[]);
        internal void Register(SqliteConnection c,SqliteTransaction tx)
        {
            using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText=$"INSERT INTO ServerInventory (ServerProfileId,AuthoritativeHostId,DisplayName,InstallPath,CreatedUtc) VALUES ('{Target.ServerProfileId:D}','{F.HostId:D}','private-name','private-path','{F.Time.Now:O}');";cmd.ExecuteNonQuery();
        }
        public void Dispose()=>F.Dispose();
    }
    public static Task EveryPreWritePathRecordsActualActorWithoutEffects()
    {
        using var r=new Rig();var before=r.Revision;bool callback=false;
        Action[] attempts=[()=>r.DenyLocal(),()=>r.Repo.IssueServer(r.Local,before,Guid.NewGuid(),r.Grantee,ServerCapability.ViewServer,r.Target,Use,null),
            ()=>r.Repo.IssueRemoteHost(r.Peer,before,Guid.NewGuid(),r.Grantee,HostCapability.CreateServer,r.F.HostId,Use,null),()=>r.DenyPeer(),
            ()=>r.Repo.ApplyPreset(r.Local,before,r.Preset()),()=>r.Repo.ApplyRemotePreset(r.Peer,before,r.Preset()),
            ()=>r.Repo.ConfigureDefaults(r.Local,before,DefaultGrantTemplate.Factory),
            ()=>r.Repo.InvalidateHostSubtree(r.Local,before,Guid.NewGuid()),()=>r.Repo.InvalidateServerSubtree(r.Local,before,Guid.NewGuid()),
            ()=>r.Repo.ReissueHistoricalPeerRoots(r.Local,before,r.F.PeerId,[],[]),
            ()=>r.Repo.CommitConfirmedRemoteCreation(r.Peer,before,r.Target,(c,tx)=>callback=true)];
        foreach(var attempt in attempts)Reject<UnauthorizedAccessException>(attempt);
        Check(r.Denials==11&&r.Revision==before&&!callback&&r.F.Count("HostCapabilityGrants")==0&&r.F.Count("ServerCapabilityGrants")==0&&r.F.Count("ServerInventory")==0&&!r.Repo.ReadDefaults().IsConfigured);
        using var cmd=r.F.Writer.CreateCommand();cmd.CommandText="SELECT ActorKind,ActorLocalPrincipalId,ActorPeerHostId,AffectedHostId,AffectedServerProfileId,Summary FROM AuditEvents WHERE EventKind='PermissionPolicyDenied';";
        using var reader=cmd.ExecuteReader();int local=0,peer=0,server=0;
        while(reader.Read())
        {
            if(reader.GetString(0)=="LocalPrincipal"){Check(reader.GetString(1)==r.User.ToString("D")&&reader.IsDBNull(2));local++;}
            else{Check(reader.IsDBNull(1)&&reader.GetString(2)==r.F.PeerId.ToString("D"));peer++;}
            Check(reader.GetString(3)==r.F.HostId.ToString("D"));
            if(!reader.IsDBNull(4)){Check(reader.GetString(4)==r.Target.ServerProfileId.ToString("D"));server++;}
            var summary=reader.GetString(5);Check(summary.Contains("Outcome=PolicyDenied")&&!summary.Contains("private-")&&!summary.Contains("fixture-public")&&!summary.Contains(new string('B',64)));
        }
        Check(local==7&&peer==4&&server==3);return Task.CompletedTask;
    }
    public static Task AuthenticationMalformedStaleAndCancellationAreNotPolicyDenials()
    {
        using var r=new Rig();var before=r.Revision;
        Reject<AuthenticationException>(()=>r.Repo.IssueHost(r.Local with{PublicVerificationKey="forged"},before,Guid.NewGuid(),r.Grantee,HostCapability.CreateServer,r.F.HostId,Use,null));
        Reject<AuthenticationException>(()=>r.Repo.IssueRemoteHost(r.Peer with{PeerFingerprint=new('C',64)},before,Guid.NewGuid(),r.Grantee,HostCapability.CreateServer,r.F.HostId,Use,null));
        Reject<StaleAuthorizationRevisionException>(()=>r.DenyLocal(revision:before-1));
        Reject<ArgumentException>(()=>r.Repo.IssueHost(r.Local,before,Guid.NewGuid(),r.Grantee,(HostCapability)999,r.F.HostId,Use,null));
        Reject<ArgumentException>(()=>r.Repo.IssueHost(r.Local,before,Guid.NewGuid(),r.Grantee,HostCapability.CreateServer,r.F.HostId,Use,Guid.Empty));
        using var ct=new CancellationTokenSource();ct.Cancel();Reject<OperationCanceledException>(()=>r.DenyLocal(ct:ct.Token));
        Check(r.Denials==0&&r.Revision==before&&r.F.Count("HostCapabilityGrants")==0);return Task.CompletedTask;
    }
    public static Task DenialAuditFailureRemovalMutationAndIdentityRaceRollBack()
    {
        foreach(var remote in new[]{false,true})foreach(var mode in Enumerable.Range(0,5))
        {
            using var r=new Rig();var before=r.Revision;
            var sql=mode switch
            {
                0=>"SELECT RAISE(ABORT,'denial audit unavailable');",
                1=>"DELETE FROM AuditEvents WHERE AuditEventId=NEW.AuditEventId;",
                2=>"UPDATE AuditEvents SET Summary='changed' WHERE AuditEventId=NEW.AuditEventId;",
                3=>remote?$"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{r.F.PeerId:D}';":$"UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE LocalPrincipalId='{r.User:D}';",
                _=>$"UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE LocalPrincipalId='{r.F.OwnerId:D}';"
            };
            r.F.Execute($"CREATE TRIGGER DenialFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PermissionPolicyDenied' BEGIN {sql} END;");
            Action apply=()=>{if(remote)r.DenyPeer();else r.DenyLocal();};
            if(mode==0)Reject<SqliteException>(apply);else if(mode is 1 or 2)Reject<InvalidOperationException>(apply);else if(mode==3)Reject<AuthenticationException>(apply);else Reject<StaleAuthorizationRevisionException>(apply);
            Check(r.Denials==0&&r.Revision==before&&r.F.Count("HostCapabilityGrants")==0&&r.F.Count("ServerCapabilityGrants")==0);
            r.F.Execute("DROP TRIGGER DenialFault;");Reject<UnauthorizedAccessException>(apply);Check(r.Denials==1);
        }
        foreach(var incarnation in new[]{false,true})
        {
            using var r=new Rig();var before=r.Revision;var sql=incarnation?
                $"DELETE FROM PeerRelationshipIncarnations WHERE PeerHostId='{r.F.PeerId:D}'; INSERT INTO PeerRelationshipIncarnations (PeerHostId) VALUES ('{r.F.PeerId:D}');":
                $"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{new string('C',64)}' WHERE CredentialRef='current';";
            r.F.Execute($"CREATE TRIGGER DenialFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PermissionPolicyDenied' BEGIN {sql} END;");
            Reject<AuthenticationException>(()=>r.DenyPeer());Check(r.Denials==0&&r.Revision==before);
        }
        return Task.CompletedTask;
    }
    public static Task PresetDenialAndCallbackFailureNeverCommitPartialWork()
    {
        using var r=new Rig();var root=Guid.NewGuid();
        r.Repo.IssueHost(r.Owner,r.Revision,root,ActorRef.LocalPrincipal(r.User),HostCapability.CreateServer,r.F.HostId,Onward,null);
        var before=r.Revision;
        var recipe=new RolePreset("private-preset-label",[new(Guid.NewGuid(),r.Grantee,HostCapability.CreateServer,r.F.HostId,Use,root),new(Guid.NewGuid(),r.Grantee,HostCapability.ManageHostUpdates,r.F.HostId,Use,root)],[]);
        Reject<UnauthorizedAccessException>(()=>r.Repo.ApplyPreset(r.Local,before,recipe));Check(r.Denials==1&&r.Revision==before&&r.F.Count("HostCapabilityGrants")==1);
        r.Repo.IssueHost(r.Owner,r.Revision,Guid.NewGuid(),r.Grantee,HostCapability.CreateServer,r.F.HostId,Use,null);before=r.Revision;
        Reject<UnauthorizedAccessException>(()=>r.Repo.CommitConfirmedRemoteCreation(r.Peer,before,r.Target,(c,tx)=>{r.Register(c,tx);throw new UnauthorizedAccessException("fixture callback failure");}));
        Check(r.Denials==1&&r.Revision==before&&r.F.Count("ServerInventory")==0&&r.F.Count("ServerCapabilityGrants")==0);
        r.Repo.CommitConfirmedRemoteCreation(r.Peer,before,r.Target,r.Register);before=r.Revision;bool called=false;
        Reject<UnauthorizedAccessException>(()=>r.Repo.CommitConfirmedRemoteCreation(r.Peer,before,r.Target,(c,tx)=>called=true));
        Check(!called&&r.Denials==2&&r.Revision==before&&r.F.Count("ServerCapabilityGrants")==6);return Task.CompletedTask;
    }
    public static Task ConcurrentDenialsKeepRevisionAndSuccessfulGrantsSeparate()
    {
        using var r=new Rig();var before=r.Revision;var actor=r.Local;var grantee=r.Grantee;int refused=0;
        Parallel.For(0,8,_=>{try{r.Repo.IssueHost(actor,before,Guid.NewGuid(),grantee,HostCapability.CreateServer,r.F.HostId,Use,null);}catch(UnauthorizedAccessException){Interlocked.Increment(ref refused);}});
        Check(refused==8&&r.Denials==8&&r.Revision==before&&r.F.Count("HostCapabilityGrants")==0);
        var root=Guid.NewGuid();r.Repo.IssueHost(r.Owner,r.Revision,root,ActorRef.LocalPrincipal(r.User),HostCapability.CreateServer,r.F.HostId,Onward,null);
        Reject<StaleAuthorizationRevisionException>(()=>r.DenyLocal(revision:before));Check(r.Denials==8);
        r.Repo.IssueHost(actor,r.Revision,Guid.NewGuid(),grantee,HostCapability.CreateServer,r.F.HostId,Use,root);
        Check(r.Denials==8&&r.F.Count("HostCapabilityGrants")==2&&r.Revision==before+2);return Task.CompletedTask;
    }
    private sealed class CancelSecond(TimeProvider actual,CancellationTokenSource cancellation):TimeProvider
    {private int count;public override DateTimeOffset GetUtcNow(){if(++count==2)cancellation.Cancel();return actual.GetUtcNow();}}
    public static Task LateCancellationAndSuccessfulAuditFailureDoNotBecomeDenials()
    {
        using var r=new Rig();var before=r.Revision;using var ct=new CancellationTokenSource();
        var repo=new GrantPolicyRepository(r.F.Database,r.F.HostId,new CancelSecond(r.F.Time,ct));
        Reject<OperationCanceledException>(()=>r.DenyLocal(repo,ct:ct.Token));Check(r.Denials==0&&r.Revision==before);
        r.F.Execute("CREATE TRIGGER SuccessFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='CapabilityGrantIssued' BEGIN SELECT RAISE(ABORT,'success audit unavailable'); END;");
        Reject<SqliteException>(()=>r.Repo.IssueHost(r.Owner,before,Guid.NewGuid(),r.Grantee,HostCapability.CreateServer,r.F.HostId,Use,null));
        Check(r.Denials==0&&r.Revision==before&&r.F.Count("HostCapabilityGrants")==0);return Task.CompletedTask;
    }
}
