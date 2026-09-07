using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;

namespace PalworldServerManager.SelfTest;

internal static class GrantPolicyPersistenceTests
{
    private static readonly DelegationRights Use=new(false,false),Onward=new(true,true);
    private static void Check(bool value){if(!value)throw new Exception("Grant persistence assertion failed.");}
    private static void Reject<T>(Action action)where T:Exception
    {try{action();}catch(T){return;}throw new Exception("Expected grant persistence refusal: "+typeof(T).Name);}
    private sealed class Rig:IDisposable
    {
        internal readonly PeerTrustTests.Fixture F=new();
        internal readonly Guid A=Guid.NewGuid(),B=Guid.NewGuid();
        internal GrantPolicyRepository Repo=>new(F.Database,F.HostId,F.Time);
        internal LocalPrincipalMutationActor Owner=>new(F.HostId,F.OwnerId,"native-owner","fixture-public");
        internal LocalPrincipalMutationActor ActorA=>new(F.HostId,A,"native-a","public-a");
        internal LocalPrincipalMutationActor ActorB=>new(F.HostId,B,"native-b","public-b");
        internal Rig()
        {
            F.Execute($"INSERT INTO LocalPrincipals (LocalPrincipalId,OsPrincipalRef,PublicVerificationKey,IsOwner,State,CreatedUtc) VALUES ('{A:D}','native-a','public-a',0,'Active','{F.Time.Now:O}'),('{B:D}','native-b','public-b',0,'Active','{F.Time.Now:O}');");
        }
        internal long Revision=>Repo.Read().Revision;
        internal long AuditCount=>HostDatabase.QueryScalarLong(F.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind LIKE 'CapabilityGrant%';");
        internal Guid Root(HostCapability cap=HostCapability.CreateServer)
        {var id=Guid.NewGuid();Repo.IssueHost(Owner,Revision,id,ActorRef.LocalPrincipal(A),cap,F.HostId,Onward,null);return id;}
        public void Dispose()=>F.Dispose();
    }
    public static Task CanonicalWritesAndRealAudit()
    {
        using var r=new Rig();var start=r.Revision;var root=r.Root();var child=Guid.NewGuid();
        r.Repo.IssueHost(r.ActorA,r.Revision,child,ActorRef.LocalPrincipal(r.B),HostCapability.CreateServer,r.F.HostId,Use,root);
        var server=new ServerRef(r.F.PeerId,Guid.NewGuid());var sr=Guid.NewGuid();var sc=Guid.NewGuid();
        r.Repo.IssueServer(r.Owner,r.Revision,sr,ActorRef.LocalPrincipal(r.A),ServerCapability.ViewServer,server,Onward,null);
        r.Repo.IssueServer(r.ActorA,r.Revision,sc,ActorRef.LocalPrincipal(r.B),ServerCapability.ViewServer,server,Use,sr);
        var s=r.Repo.Read();Check(s.Revision==start+4&&r.AuditCount==4&&s.HostGrants.Count==2&&s.ServerGrants.Count==2);
        Check(s.Policy.CanUseHost(ActorRef.LocalPrincipal(r.B),HostCapability.CreateServer,r.F.HostId));
        Check(s.Policy.CanUseServer(ActorRef.LocalPrincipal(r.B),ServerCapability.ViewServer,server));
        Check(!s.Policy.CanUseServer(ActorRef.LocalPrincipal(r.B),ServerCapability.ViewServer,new(r.F.HostId,server.ServerProfileId)));
        Check(s.HostGrants.Single(g=>g.GrantId==child).GrantedByActor==ActorRef.LocalPrincipal(r.A));
        using var cmd=r.F.Writer.CreateCommand();cmd.CommandText="SELECT ActorLocalPrincipalId,AffectedHostId,Summary FROM AuditEvents WHERE EventKind='CapabilityGrantIssued' ORDER BY rowid;";
        using var reader=cmd.ExecuteReader();var rows=new List<(string Actor,string Host,string Summary)>();while(reader.Read())rows.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2)));
        Check(rows[1].Actor==r.A.ToString("D")&&rows[1].Summary.Contains(root.ToString("D"))&&rows[1].Summary.Contains(child.ToString("D")));
        Check(rows[3].Host==r.F.PeerId.ToString("D")&&rows.All(x=>!x.Summary.Contains("public-a")&&!x.Summary.Contains("native-")));
        return Task.CompletedTask;
    }
    public static Task DenialsAndFreshLocalProof()
    {
        using var r=new Rig();var root=r.Root();var revision=r.Revision;var audit=r.AuditCount;
        void Issue(LocalPrincipalMutationActor actor,HostCapability cap,Guid target,Guid? source)=>r.Repo.IssueHost(actor,r.Revision,Guid.NewGuid(),ActorRef.LocalPrincipal(r.B),cap,target,Use,source);
        Reject<UnauthorizedAccessException>(()=>Issue(r.ActorB,HostCapability.CreateServer,r.F.HostId,root));
        Reject<UnauthorizedAccessException>(()=>Issue(r.ActorA,HostCapability.ManageHostUpdates,r.F.HostId,root));
        Reject<UnauthorizedAccessException>(()=>Issue(r.ActorA,HostCapability.CreateServer,r.F.PeerId,root));
        Reject<UnauthorizedAccessException>(()=>Issue(r.ActorA,HostCapability.CreateServer,r.F.HostId,null));
        Reject<AuthenticationException>(()=>Issue(r.Owner with {PublicVerificationKey="stale"},HostCapability.CreateServer,r.F.HostId,null));
        Reject<AuthenticationException>(()=>Issue(r.ActorA with {OsPrincipalRef="native-owner"},HostCapability.CreateServer,r.F.HostId,root));
        Reject<AuthenticationException>(()=>Issue(r.Owner with {HostId=Guid.NewGuid()},HostCapability.CreateServer,r.F.HostId,null));
        Reject<UnauthorizedAccessException>(()=>r.Repo.IssueHost(r.Owner,r.Revision,root,ActorRef.LocalPrincipal(r.A),HostCapability.CreateServer,r.F.HostId,Use,null));
        Check(r.Revision==revision&&r.AuditCount==audit&&r.F.Count("HostCapabilityGrants")==1);
        r.F.Execute($"UPDATE LocalPrincipals SET PublicVerificationKey='rotated' WHERE LocalPrincipalId='{r.A:D}';");
        Reject<AuthenticationException>(()=>Issue(r.ActorA,HostCapability.CreateServer,r.F.HostId,root));
        Issue(r.ActorA with {PublicVerificationKey="rotated"},HostCapability.CreateServer,r.F.HostId,root);
        Check(r.Repo.Read().Policy.CanUseHost(ActorRef.LocalPrincipal(r.A),HostCapability.CreateServer,r.F.HostId));
        return Task.CompletedTask;
    }
    public static Task ExactOwnerSubtreeAndExistingRevocation()
    {
        using var r=new Rig();var root=r.Root();var unrelated=r.Root(HostCapability.ManageHostSettings);var child=Guid.NewGuid();var grand=Guid.NewGuid();
        r.Repo.IssueHost(r.ActorA,r.Revision,child,ActorRef.LocalPrincipal(r.B),HostCapability.CreateServer,r.F.HostId,Onward,root);
        r.Repo.IssueHost(r.ActorB,r.Revision,grand,ActorRef.LocalPrincipal(r.A),HostCapability.CreateServer,r.F.HostId,Use,child);
        var target=new ServerRef(r.F.HostId,Guid.NewGuid());
        r.Repo.IssueServer(r.Owner,r.Revision,root,ActorRef.LocalPrincipal(r.A),ServerCapability.ViewServer,target,Onward,null);
        var sChild=Guid.NewGuid();r.Repo.IssueServer(r.ActorA,r.Revision,sChild,ActorRef.LocalPrincipal(r.B),ServerCapability.ViewServer,target,Use,root);
        Reject<UnauthorizedAccessException>(()=>r.Repo.InvalidateHostSubtree(r.ActorA,r.Revision,root));
        var before=r.Revision;var result=r.Repo.InvalidateHostSubtree(r.Owner,before,root);var s=r.Repo.Read();
        Check(result.ChangedGrants==3&&s.Revision==before+3&&s.HostGrants.Count(g=>g.InvalidatedUtc is not null)==3);
        Check(s.Policy.CanUseHost(ActorRef.LocalPrincipal(r.A),HostCapability.ManageHostSettings,r.F.HostId));
        Check(s.Policy.CanUseServer(ActorRef.LocalPrincipal(r.B),ServerCapability.ViewServer,target)&&s.Policy.IsOwner(ActorRef.LocalPrincipal(r.F.OwnerId)));
        var audits=r.AuditCount;Check(r.Repo.InvalidateHostSubtree(r.Owner,s.Revision,root).ChangedGrants==0&&r.Revision==s.Revision&&r.AuditCount==audits);
        Check(r.Repo.InvalidateServerSubtree(r.Owner,r.Revision,root).ChangedGrants==2);
        before=r.Revision;new LocalEnrollmentRepository(r.F.Database,r.F.HostId,r.F.Time).RevokePrincipal(r.Owner,r.A);
        Check(r.Revision>before&&!r.Repo.Read().Policy.IsActive(ActorRef.LocalPrincipal(r.A)));
        Check(r.Repo.Read().HostGrants.Single(g=>g.GrantId==unrelated).InvalidatedUtc is not null);
        Reject<StaleAuthorizationRevisionException>(()=>r.Repo.InvalidateHostSubtree(r.Owner,before,unrelated));
        return Task.CompletedTask;
    }
    public static Task AuditFailureAndPostAuditMutationRollback()
    {
        foreach(var mutation in new[]{"SELECT RAISE(ABORT,'fixture audit failure');",
            "UPDATE LocalPrincipals SET PublicVerificationKey='rotated' WHERE IsOwner=1;",
            "UPDATE HostCapabilityGrants SET CanDelegate=1,CanDelegateOnwardDelegation=1;"})
        {
            using var r=new Rig();var before=r.Revision;var audits=r.AuditCount;
            r.F.Execute($"CREATE TRIGGER GrantAuditFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='CapabilityGrantIssued' BEGIN {mutation} END;");
            void Issue()=>r.Repo.IssueHost(r.Owner,before,Guid.NewGuid(),ActorRef.LocalPrincipal(r.A),HostCapability.CreateServer,r.F.HostId,Use,null);
            if(mutation.StartsWith("SELECT",StringComparison.Ordinal))Reject<SqliteException>(Issue);
            else if(mutation.Contains("LocalPrincipals",StringComparison.Ordinal))Reject<AuthenticationException>(Issue);
            else Reject<StaleAuthorizationRevisionException>(Issue);
            Check(r.Revision==before&&r.AuditCount==audits&&r.F.Count("HostCapabilityGrants")==0);
            Check(HostDatabase.QueryScalarText(r.F.Writer,"SELECT PublicVerificationKey FROM LocalPrincipals WHERE IsOwner=1;")=="fixture-public");
        }
        using(var r=new Rig())
        {
            var root=r.Root();var child=Guid.NewGuid();r.Repo.IssueHost(r.ActorA,r.Revision,child,ActorRef.LocalPrincipal(r.B),HostCapability.CreateServer,r.F.HostId,Use,root);
            var before=r.Revision;var audits=r.AuditCount;
            r.F.Execute("CREATE TRIGGER RevokeAuditFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='CapabilityGrantSubtreeInvalidated' BEGIN SELECT RAISE(ABORT,'fixture revoke audit failure'); END;");
            Reject<SqliteException>(()=>r.Repo.InvalidateHostSubtree(r.Owner,before,root));
            Check(r.Revision==before&&r.AuditCount==audits&&r.Repo.Read().HostGrants.All(g=>g.InvalidatedUtc is null));
            r.F.Execute("DROP TRIGGER RevokeAuditFault;");
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            Reject<OperationCanceledException>(()=>r.Repo.InvalidateHostSubtree(r.Owner,before,root,cancelled.Token));
            Check(r.Revision==before);Check(r.Repo.InvalidateHostSubtree(r.Owner,before,root).ChangedGrants==2);
        }
        return Task.CompletedTask;
    }
    private sealed class CancelAtWrite(TimeProvider clock,CancellationTokenSource cancellation):TimeProvider
    {
        public override DateTimeOffset GetUtcNow(){cancellation.Cancel();return clock.GetUtcNow();}
    }
    public static Task PostAuditRevokeChangesAndLateCancellation()
    {
        foreach(var mutation in new[]{"UPDATE LocalPrincipals SET PublicVerificationKey='rotated' WHERE IsOwner=1;",
            "UPDATE ServerCapabilityGrants SET InvalidatedUtc=NULL;"})
        {
            using var r=new Rig();var root=Guid.NewGuid();var target=new ServerRef(r.F.HostId,Guid.NewGuid());
            r.Repo.IssueServer(r.Owner,r.Revision,root,ActorRef.LocalPrincipal(r.A),ServerCapability.ViewServer,target,Use,null);
            var before=r.Revision;var audits=r.AuditCount;
            r.F.Execute($"CREATE TRIGGER RevokeAuditMutation AFTER INSERT ON AuditEvents WHEN NEW.EventKind='CapabilityGrantSubtreeInvalidated' BEGIN {mutation} END;");
            void Revoke()=>r.Repo.InvalidateServerSubtree(r.Owner,before,root);
            if(mutation.Contains("LocalPrincipals",StringComparison.Ordinal))Reject<AuthenticationException>(Revoke);
            else Reject<StaleAuthorizationRevisionException>(Revoke);
            Check(r.Revision==before&&r.AuditCount==audits&&r.Repo.Read().ServerGrants.Single().InvalidatedUtc is null);
        }
        using(var r=new Rig())
        {
            var before=r.Revision;var audits=r.AuditCount;using var cancel=new CancellationTokenSource();
            var repo=new GrantPolicyRepository(r.F.Database,r.F.HostId,new CancelAtWrite(r.F.Time,cancel));
            Reject<OperationCanceledException>(()=>repo.IssueHost(r.Owner,before,Guid.NewGuid(),ActorRef.LocalPrincipal(r.A),HostCapability.CreateServer,r.F.HostId,Use,null,cancel.Token));
            Check(r.Revision==before&&r.AuditCount==audits&&r.F.Count("HostCapabilityGrants")==0);
        }
        return Task.CompletedTask;
    }
    public static Task ConcurrentWritersAndActorTrustRevisions()
    {
        using var r=new Rig();var revision=r.Revision;int won=0,stale=0;
        Parallel.For(0,8,_=>
        {
            try{r.Repo.IssueHost(r.Owner,revision,Guid.NewGuid(),ActorRef.LocalPrincipal(r.A),HostCapability.CreateServer,r.F.HostId,Use,null);Interlocked.Increment(ref won);}
            catch(StaleAuthorizationRevisionException){Interlocked.Increment(ref stale);}
        });
        Check(won==1&&stale==7&&r.Revision==revision+1&&r.AuditCount==1);
        var snapshot=r.Repo.Read();r.F.Repository.RecordVerifiedBinding(r.F.PeerId,new string('B',64),new string('A',64));
        Check(r.Revision>snapshot.Revision&&!r.Repo.Read().Policy.IsActive(ActorRef.RemoteManager(r.F.PeerId)));
        Reject<StaleAuthorizationRevisionException>(()=>r.Repo.IssueHost(r.Owner,snapshot.Revision,Guid.NewGuid(),ActorRef.LocalPrincipal(r.A),HostCapability.CreateServer,r.F.HostId,Use,null));
        Reject<UnauthorizedAccessException>(()=>r.Repo.IssueHost(r.Owner,r.Revision,Guid.NewGuid(),ActorRef.RemoteManager(r.F.PeerId),HostCapability.CreateServer,r.F.HostId,Use,null));
        r.F.Execute("CREATE TABLE ActivationRpcEffects (Peer TEXT PRIMARY KEY);");
        r.F.Repository.AcceptActivationAcknowledgement(r.F.PeerId,new string('B',64),new string('A',64),new(r.F.PeerId,r.F.HostId,new string('A',64)),new LocalOwnerActivationTests.Hook());
        var grant=Guid.NewGuid();r.Repo.IssueHost(r.Owner,r.Revision,grant,ActorRef.RemoteManager(r.F.PeerId),HostCapability.CreateServer,r.F.HostId,Use,null);
        snapshot=r.Repo.Read();Check(snapshot.Policy.CanUseHost(ActorRef.RemoteManager(r.F.PeerId),HostCapability.CreateServer,r.F.HostId));
        r.F.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{r.F.PeerId:D}';");
        Check(r.Revision>snapshot.Revision&&!r.Repo.Read().Policy.CanUseHost(ActorRef.RemoteManager(r.F.PeerId),HostCapability.CreateServer,r.F.HostId));
        return Task.CompletedTask;
    }
    public static Task UpgradePreservesGrantsAndRevisionFailsClosed()
    {
        using(var f=new PeerTrustTests.Fixture(schemaVersion:9))
        {
            var grant=Guid.NewGuid();f.Execute($"INSERT INTO HostCapabilityGrants (GrantId,TargetHostId,Capability,GranteeActorKind,GranteeLocalPrincipalId,GrantedByActorKind,GrantedByLocalPrincipalId,CanDelegate,CanDelegateOnwardDelegation,CreatedUtc) VALUES ('{grant:D}','{f.HostId:D}','CreateServer','LocalPrincipal','{f.OwnerId:D}','LocalPrincipal','{f.OwnerId:D}',0,0,'{f.Time.Now:O}');");
            var audits=f.Count("AuditEvents");var throughRevision=new HostSchemaMigrationRunner(HostSchema.AllMigrations().Take(10));
            Check(throughRevision.Migrate(f.Writer)==1);Check(throughRevision.Migrate(f.Writer)==0);
            var s=new GrantPolicyRepository(f.Database,f.HostId).Read();Check(s.Revision==0&&s.HostGrants.Single().GrantId==grant&&f.Count("AuditEvents")==audits);
        }
        foreach(var missing in new[]{false,true})
        {
            using var r=new Rig();var count=r.F.Count("HostCapabilityGrants");
            r.F.Execute(missing?"DELETE FROM AuthorizationRevision;":"UPDATE AuthorizationRevision SET Revision=9223372036854775807;");
            if(missing)Reject<InvalidDataException>(()=>r.Repo.Read());
            Reject<SqliteException>(()=>r.F.Execute("UPDATE HostIdentity SET HostBootstrapState=HostBootstrapState;"));
            Check(r.F.Count("HostCapabilityGrants")==count);
            if(!missing)Reject<SqliteException>(()=>r.Repo.IssueHost(r.Owner,long.MaxValue,Guid.NewGuid(),ActorRef.LocalPrincipal(r.A),HostCapability.CreateServer,r.F.HostId,Use,null));
        }
        using(var r=new Rig())
        {
            var root=r.Root();r.F.Execute($"UPDATE HostCapabilityGrants SET Capability='1' WHERE GrantId='{root:D}';");
            Reject<InvalidDataException>(()=>r.Repo.Read());
        }
        return Task.CompletedTask;
    }
}
