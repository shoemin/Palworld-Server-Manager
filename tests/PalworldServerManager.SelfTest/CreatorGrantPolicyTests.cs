using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static class CreatorGrantPolicyTests
{
    private static readonly DelegationRights Use=new(false,false);
    private static readonly ServerCapability[] Approved=[ServerCapability.ViewServer,ServerCapability.StartStopRestart,ServerCapability.EditSettings,ServerCapability.ManageBackups,ServerCapability.TransferExport,ServerCapability.DeleteServer];
    private static void Check(bool value){if(!value)throw new Exception("Creator grant assertion failed.");}
    private static void Reject<T>(Action action)where T:Exception
    {try{action();}catch(T){return;}throw new Exception("Expected creator refusal: "+typeof(T).Name);}
    private sealed class Rig:IDisposable
    {
        internal readonly PeerTrustTests.Fixture F=new();
        internal readonly Guid Root=Guid.NewGuid(),Other=Guid.NewGuid();
        internal readonly ServerRef Target;
        internal GrantPolicyRepository Repo=>new(F.Database,F.HostId,F.Time);
        internal LocalPrincipalMutationActor Owner=>new(F.HostId,F.OwnerId,"native-owner","fixture-public");
        internal long Revision=>Repo.Read().Revision;
        internal PeerGrantMutationActor Actor=>new(F.HostId,F.PeerId,new('B',64),new('A',64),HostDatabase.QueryScalarLong(F.Writer,$"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{F.PeerId:D}';"));
        internal Rig(bool authorize=true)
        {
            Target=new(F.HostId,Guid.NewGuid());
            foreach(var peer in new[]{F.PeerId,Other})F.Execute($"INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('{peer:D}','Active','{new string('B',64)}','{F.Time.Now:O}');");
            if(authorize)Repo.IssueHost(Owner,Revision,Root,ActorRef.RemoteManager(F.PeerId),HostCapability.CreateServer,F.HostId,Use,null);
        }
        internal static void Sql(SqliteConnection c,SqliteTransaction tx,string sql){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;cmd.ExecuteNonQuery();}
        internal void Register(SqliteConnection c,SqliteTransaction tx)=>Sql(c,tx,$"INSERT INTO ServerInventory (ServerProfileId,AuthoritativeHostId,DisplayName,InstallPath,CreatedUtc) VALUES ('{Target.ServerProfileId:D}','{Target.AuthoritativeHostId:D}','fixture-server','private-fixture-path','{F.Time.Now:O}');");
        internal CreatorGrantResult Apply(long? revision=null,PeerGrantMutationActor? actor=null,Action<SqliteConnection,SqliteTransaction>? confirm=null)
            =>Repo.CommitConfirmedRemoteCreation(actor??Actor,revision??Revision,Target,confirm??Register);
        public void Dispose()=>F.Dispose();
    }
    public static Task SixCanonicalCandidatesHaveExactScopeAndNoDelegation()
    {
        using var r=new Rig();var before=r.Repo.Read();var peer=ActorRef.RemoteManager(r.F.PeerId);
        var grants=before.Policy.ExpandRemoteCreatorGrants(r.F.PeerId,r.Target,r.F.Time.Now);
        Check(grants.Count==6&&grants.Select(g=>g.Capability).Order().SequenceEqual(Approved.Order())&&grants.Select(g=>g.GrantId).Distinct().Count()==6);
        Check(grants.All(g=>g.GranteeActor==peer&&g.Target==r.Target&&g.Rights==Use&&g.GrantedByActor==ActorRef.LocalPrincipal(r.F.OwnerId)&&g.DerivedFromGrantId is null));
        Check(before.ServerGrants.Count==0&&!before.Policy.CanUseServer(peer,ServerCapability.ViewServer,r.Target));
        Reject<NotSupportedException>(()=>((IList<ServerCapabilityGrant>)grants).Clear());
        Reject<UnauthorizedAccessException>(()=>before.Policy.ExpandRemoteCreatorGrants(r.Other,r.Target,r.F.Time.Now));
        Reject<UnauthorizedAccessException>(()=>before.Policy.ExpandRemoteCreatorGrants(r.F.PeerId,new(r.Other,r.Target.ServerProfileId),r.F.Time.Now));
        var issued=new AuthorizationPolicy(r.F.HostId,r.F.OwnerId,[r.F.OwnerId],[r.F.PeerId,r.Other],before.HostGrants,grants);
        Reject<UnauthorizedAccessException>(()=>issued.IssueServer(peer,Guid.NewGuid(),ActorRef.RemoteManager(r.Other),ServerCapability.ViewServer,r.Target,Use,grants[0].GrantId,r.F.Time.Now));
        Check(!issued.CanUseServer(peer,ServerCapability.ManageServerSharing,r.Target));return Task.CompletedTask;
    }
    public static Task ConfirmedCreationCommitsSixWithRealActorAudit()
    {
        using var r=new Rig();var before=r.Revision;var initial=r.F.Time.Now;
        var result=r.Apply(confirm:(c,tx)=>{Check(HostDatabase.QueryScalarLong(c,"SELECT COUNT(*) FROM ServerCapabilityGrants;")==0);r.F.Time.Now+=TimeSpan.FromMinutes(1);r.Register(c,tx);});
        var snapshot=r.Repo.Read();Check(result.Revision==before+6&&result.GrantIds.Count==6&&r.F.Count("ServerInventory")==1);
        Check(snapshot.ServerGrants.All(g=>g.GrantedUtc>initial&&g.GrantedUtc==r.F.Time.Now&&g.Target==r.Target&&g.Rights==Use));
        Check(!snapshot.Policy.CanUseServer(ActorRef.RemoteManager(r.Other),ServerCapability.ViewServer,r.Target));
        using var cmd=r.F.Writer.CreateCommand();cmd.CommandText="SELECT ActorKind,ActorLocalPrincipalId,ActorPeerHostId,AffectedHostId,AffectedServerProfileId,Summary FROM AuditEvents WHERE EventKind='CreatorGrantApplied';";
        using var reader=cmd.ExecuteReader();int count=0;
        while(reader.Read())
        {
            var summary=reader.GetString(5);Check(reader.GetString(0)=="RemoteManager"&&reader.IsDBNull(1)&&reader.GetString(2)==r.F.PeerId.ToString("D"));
            Check(reader.GetString(3)==r.F.HostId.ToString("D")&&reader.GetString(4)==r.Target.ServerProfileId.ToString("D"));
            Check(summary.Contains("Source=OwnerRoot")&&summary.Contains("Issuer=LocalPrincipal:"+r.F.OwnerId.ToString("D"))&&summary.Contains(result.CreationEventId.ToString("D")));
            Check(!summary.Contains("private-fixture-path")&&!summary.Contains("fixture-public")&&!summary.Contains(new string('B',64)));count++;
        }
        Check(count==6);Reject<NotSupportedException>(()=>((IList<Guid>)result.GrantIds).Clear());return Task.CompletedTask;
    }
    public static Task FailedMissingAndExistingCreationNeverGrant()
    {
        foreach(var mode in new[]{0,1,2})
        {
            using var r=new Rig();var before=r.Revision;var audits=r.F.Count("AuditEvents");
            Reject<InvalidOperationException>(()=>r.Apply(confirm:(c,tx)=>{if(mode==1)r.Register(c,tx);if(mode!=2)throw new InvalidOperationException("Creation failed.");}));
            Check(r.Revision==before&&r.F.Count("ServerInventory")==0&&r.F.Count("ServerCapabilityGrants")==0&&r.F.Count("AuditEvents")==audits);
        }
        using(var r=new Rig())
        {
            r.Apply();var before=r.Revision;bool called=false;
            Reject<UnauthorizedAccessException>(()=>r.Apply(confirm:(c,tx)=>called=true));Check(!called&&r.Revision==before&&r.F.Count("ServerCapabilityGrants")==6);
            r.F.Execute("DELETE FROM ServerInventory;");
            Reject<UnauthorizedAccessException>(()=>r.Apply(confirm:(c,tx)=>called=true));Check(!called&&r.Revision==before);
        }
        return Task.CompletedTask;
    }
    public static Task CurrentTransportAndCreateAuthorityAreRequired()
    {
        using(var r=new Rig())
        {
            var actor=r.Actor;var before=r.Revision;bool called=false;
            foreach(var bad in new[]{actor with{HostId=Guid.NewGuid()},actor with{PeerHostId=Guid.NewGuid()},actor with{PeerFingerprint=new('C',64)},actor with{LocalFingerprint=new('D',64)},actor with{Incarnation=actor.Incarnation+1}})
                Reject<AuthenticationException>(()=>r.Apply(actor:bad,confirm:(c,tx)=>called=true));
            Reject<StaleAuthorizationRevisionException>(()=>r.Apply(before-1,confirm:(c,tx)=>called=true));
            r.F.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{r.F.PeerId:D}';");
            Reject<AuthenticationException>(()=>r.Apply(actor:actor,confirm:(c,tx)=>called=true));Check(!called&&r.F.Count("ServerCapabilityGrants")==0);
        }
        using(var r=new Rig(false))
        {
            r.Repo.IssueHost(r.Owner,r.Revision,Guid.NewGuid(),ActorRef.RemoteManager(r.F.PeerId),HostCapability.ManagePermissions,r.F.HostId,new(true,true),null);
            r.Repo.IssueHost(r.Owner,r.Revision,Guid.NewGuid(),ActorRef.RemoteManager(r.F.PeerId),HostCapability.CreateServer,r.Other,Use,null);
            bool called=false;Reject<UnauthorizedAccessException>(()=>r.Apply(confirm:(c,tx)=>called=true));Check(!called&&r.F.Count("ServerCapabilityGrants")==0);
        }
        return Task.CompletedTask;
    }
    public static Task ConfirmationAndAuditMutationsRollBackEverything()
    {
        foreach(var mode in Enumerable.Range(0,8))
        {
            using var r=new Rig();var before=r.Revision;var audits=r.F.Count("AuditEvents");var actor=r.Actor;
            var mutation=mode switch
            {
                0=>"SELECT RAISE(ABORT,'creator audit failed');",
                1=>$"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{r.F.PeerId:D}';",
                2=>$"DELETE FROM PeerRelationshipIncarnations WHERE PeerHostId='{r.F.PeerId:D}'; INSERT INTO PeerRelationshipIncarnations (PeerHostId) VALUES ('{r.F.PeerId:D}');",
                3=>$"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{new string('C',64)}' WHERE CredentialRef='current';",
                4=>"UPDATE ServerInventory SET DisplayName='changed';",
                5=>$"UPDATE HostCapabilityGrants SET InvalidatedUtc='{r.F.Time.Now:O}' WHERE GrantId='{r.Root:D}';",
                6=>$"UPDATE LocalPrincipals SET PublicVerificationKey='changed-owner-key' WHERE LocalPrincipalId='{r.F.OwnerId:D}';",
                _=>"UPDATE ServerCapabilityGrants SET Capability='ManageServerSharing' WHERE Capability='DeleteServer';"
            };
            r.F.Execute($"CREATE TRIGGER CreatorFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='CreatorGrantApplied' AND NEW.Summary LIKE '%Capability=DeleteServer;%' BEGIN {mutation} END;");
            if(mode==0)Reject<SqliteException>(()=>r.Apply(actor:actor));else if(mode is 1 or 2 or 3)Reject<AuthenticationException>(()=>r.Apply(actor:actor));else if(mode==4)Reject<UnauthorizedAccessException>(()=>r.Apply(actor:actor));else Reject<StaleAuthorizationRevisionException>(()=>r.Apply(actor:actor));
            Check(r.Revision==before&&r.F.Count("ServerInventory")==0&&r.F.Count("ServerCapabilityGrants")==0&&r.F.Count("AuditEvents")==audits&&r.Actor==actor);
            r.F.Execute("DROP TRIGGER CreatorFault;");Check(r.Apply().GrantIds.Count==6);
        }
        using(var r=new Rig())
        {
            var before=r.Revision;Reject<StaleAuthorizationRevisionException>(()=>r.Apply(confirm:(c,tx)=>{r.Register(c,tx);Rig.Sql(c,tx,$"UPDATE HostCapabilityGrants SET InvalidatedUtc='{r.F.Time.Now:O}' WHERE GrantId='{r.Root:D}';");}));
            Check(r.Revision==before&&r.F.Count("ServerInventory")==0&&r.F.Count("ServerCapabilityGrants")==0);
        }
        return Task.CompletedTask;
    }
    public static Task ConcurrentFinalizationAndCreateRevocationStaySeparate()
    {
        using var r=new Rig();var before=r.Revision;var actor=r.Actor;int won=0,stale=0;
        Parallel.For(0,8,_=>{try{r.Apply(before,actor);Interlocked.Increment(ref won);}catch(StaleAuthorizationRevisionException){Interlocked.Increment(ref stale);}});
        Check(won==1&&stale==7&&r.F.Count("ServerInventory")==1&&r.F.Count("ServerCapabilityGrants")==6);
        r.Repo.InvalidateHostSubtree(r.Owner,r.Revision,r.Root);
        var policy=r.Repo.Read().Policy;var peer=ActorRef.RemoteManager(r.F.PeerId);
        Check(!policy.CanUseHost(peer,HostCapability.CreateServer,r.F.HostId)&&Approved.All(cap=>policy.CanUseServer(peer,cap,r.Target)));
        return Task.CompletedTask;
    }
    public static Task MachineGrantDoesNotAuthorizeEveryLocalUser()
    {
        using var r=new Rig();r.Apply();var remote=r.Repo.Read().Policy;
        var owner=ActorRef.LocalPrincipal(Guid.NewGuid());var user=ActorRef.LocalPrincipal(Guid.NewGuid());
        var local=new AuthorizationPolicy(r.F.PeerId,owner.Id,[owner.Id,user.Id],[r.F.HostId],[],[]);
        Check(!RemoteAuthorization.CanUseServer(local,user,remote,ServerCapability.ViewServer,r.Target));
        var grant=local.IssueServer(owner,Guid.NewGuid(),user,ServerCapability.ViewServer,r.Target,Use,null,r.F.Time.Now);
        var allowed=new AuthorizationPolicy(r.F.PeerId,owner.Id,[owner.Id,user.Id],[r.F.HostId],[],[grant]);
        Check(RemoteAuthorization.CanUseServer(allowed,user,remote,ServerCapability.ViewServer,r.Target));
        Check(!RemoteAuthorization.CanUseServer(allowed,user,remote,ServerCapability.DeleteServer,r.Target));return Task.CompletedTask;
    }
    private sealed class CancelAtGrant(TimeProvider actual,CancellationTokenSource cancellation):TimeProvider
    {public override DateTimeOffset GetUtcNow(){cancellation.Cancel();return actual.GetUtcNow();}}
    public static Task PendingPinAndCancellationRespectCurrentEvidence()
    {
        using(var r=new Rig())
        {
            r.F.Execute($"UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint='{new string('C',64)}',PendingRotationId='{Guid.NewGuid():D}',PendingRotationExpiresUtc='{r.F.Time.Now.AddMinutes(-1):O}',PendingReconfirmationRequired=1 WHERE PeerHostId='{r.F.PeerId:D}';");
            Check(r.Apply(actor:r.Actor with{PeerFingerprint=new('C',64)}).GrantIds.Count==6);
        }
        using(var r=new Rig())
        {
            var before=r.Revision;var audits=r.F.Count("AuditEvents");using var ct=new CancellationTokenSource();
            var repo=new GrantPolicyRepository(r.F.Database,r.F.HostId,new CancelAtGrant(r.F.Time,ct));
            Reject<OperationCanceledException>(()=>repo.CommitConfirmedRemoteCreation(r.Actor,before,r.Target,r.Register,ct.Token));
            Check(r.Revision==before&&r.F.Count("ServerInventory")==0&&r.F.Count("ServerCapabilityGrants")==0&&r.F.Count("AuditEvents")==audits);
        }
        return Task.CompletedTask;
    }
}
