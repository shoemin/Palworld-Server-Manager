using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;

namespace PalworldServerManager.SelfTest;

internal static class DefaultGrantPolicyTests
{
    private static readonly string Local=new('A',64),Peer=new('B',64);
    private static readonly DelegationRights Use=new(false,false),Onward=new(true,true);
    private static void Check(bool value){if(!value)throw new Exception("Default grant policy assertion failed.");}
    private static void Reject<T>(Action action)where T:Exception
    {try{action();}catch(T){return;}throw new Exception("Expected default policy refusal: "+typeof(T).Name);}
    private sealed class Rig:IDisposable
    {
        internal readonly PeerTrustTests.Fixture F=new();
        internal GrantPolicyRepository Repo=>new(F.Database,F.HostId,F.Time);
        internal LocalPrincipalMutationActor Owner=>new(F.HostId,F.OwnerId,"native-owner","fixture-public");
        internal DefaultGrantTemplateSnapshot Configure(DefaultGrantTemplate template)=>Repo.ConfigureDefaults(Owner,Repo.Read().Revision,template);
        internal void Bind(Guid peer)=>F.Repository.RecordVerifiedBinding(peer,Peer,Local);
        internal PeerActivationDisposition Activate(Guid peer,IPeerActivationHook? hook=null)
            =>F.Repository.AcceptActivationAcknowledgement(peer,Peer,Local,new(peer,F.HostId,Local),hook??Repo.CreateDefaultActivationHook());
        internal long Applied=>HostDatabase.QueryScalarLong(F.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='DefaultGrantApplied';");
        public void Dispose()=>F.Dispose();
    }
    private static DefaultGrantTemplate HostOnly(HostCapability cap,DelegationRights? rights=null)=>new([new(cap,rights??Use)],[]);
    public static Task FactoryShapesAndUpgrade()
    {
        Check(DefaultGrantTemplate.Factory.Hosts.Count==0&&DefaultGrantTemplate.Factory.Servers.Count==0);
        var hs=new List<HostDefaultGrant>{new(HostCapability.CreateServer,Use)};var template=new DefaultGrantTemplate(hs,[]);hs.Clear();Check(template.Hosts.Count==1);
        Reject<NotSupportedException>(()=>((IList<HostDefaultGrant>)template.Hosts).Clear());
        Reject<ArgumentException>(()=>new HostDefaultGrant((HostCapability)0,Use));
        Reject<ArgumentException>(()=>new ServerDefaultGrant((ServerCapability)99,new(Guid.NewGuid(),Guid.NewGuid()),Use));
        Reject<ArgumentException>(()=>new DefaultGrantTemplate([new(HostCapability.CreateServer,Use),new(HostCapability.CreateServer,Onward)],[]));
        using(var f=new PeerTrustTests.Fixture(schemaVersion:10))
        {
            var audits=f.Count("AuditEvents");var revision=HostDatabase.QueryScalarLong(f.Writer,"SELECT Revision FROM AuthorizationRevision;");
            var throughDefaults=new HostSchemaMigrationRunner(HostSchema.AllMigrations().Take(11));Check(throughDefaults.Migrate(f.Writer)==1&&throughDefaults.Migrate(f.Writer)==0);
            var snapshot=new GrantPolicyRepository(f.Database,f.HostId).ReadDefaults();Check(!snapshot.IsConfigured&&snapshot.Template.Hosts.Count==0&&snapshot.Template.Servers.Count==0);
            Check(snapshot.Revision==revision&&f.Count("AuditEvents")==audits&&f.Count("HostCapabilityGrants")==0&&f.Count("ServerCapabilityGrants")==0);
        }
        using(var r=new Rig())
        {
            r.Bind(r.F.PeerId);r.Activate(r.F.PeerId);Check(r.Applied==0&&r.Repo.Read().HostGrants.Count==0&&r.Repo.Read().ServerGrants.Count==0);
            var configured=r.Configure(DefaultGrantTemplate.Factory);Check(configured.IsConfigured&&configured.ConfiguredByLocalPrincipalId==r.F.OwnerId);
            Check(r.Repo.ReadDefaults().ConfigurationId==configured.ConfigurationId);
        }
        return Task.CompletedTask;
    }
    public static Task OnlyFreshOwnerMayConfigure()
    {
        using var r=new Rig();var actor=Guid.NewGuid();r.F.Execute($"INSERT INTO LocalPrincipals (LocalPrincipalId,OsPrincipalRef,PublicVerificationKey,IsOwner,State,CreatedUtc) VALUES ('{actor:D}','native-user','public-user',0,'Active','{r.F.Time.Now:O}');");
        r.Repo.IssueHost(r.Owner,r.Repo.Read().Revision,Guid.NewGuid(),ActorRef.LocalPrincipal(actor),HostCapability.ManagePermissions,r.F.HostId,Onward,null);
        var user=new LocalPrincipalMutationActor(r.F.HostId,actor,"native-user","public-user");var before=r.Repo.Read();var audits=r.F.Count("AuditEvents");
        Reject<UnauthorizedAccessException>(()=>r.Repo.ConfigureDefaults(user,before.Revision,HostOnly(HostCapability.ManageHostUpdates)));
        Reject<AuthenticationException>(()=>r.Repo.ConfigureDefaults(r.Owner with {PublicVerificationKey="wrong"},before.Revision,DefaultGrantTemplate.Factory));
        Reject<StaleAuthorizationRevisionException>(()=>r.Repo.ConfigureDefaults(r.Owner,before.Revision-1,DefaultGrantTemplate.Factory));
        Reject<ArgumentException>(()=>r.Configure(new([],[new(ServerCapability.ViewServer,new(r.F.PeerId,Guid.NewGuid()),Use)])));
        Check(r.Repo.Read().Revision==before.Revision&&r.F.Count("AuditEvents")==audits&&!r.Repo.ReadDefaults().IsConfigured);
        return Task.CompletedTask;
    }
    public static Task CurrentActivationDefaultsAndNoRetroactivity()
    {
        using var r=new Rig();var server=new ServerRef(r.F.HostId,Guid.NewGuid());r.Bind(r.F.PeerId);
        r.Configure(HostOnly(HostCapability.CreateServer));
        var current=r.Configure(new([new(HostCapability.ManageHostUpdates,Use)],[new(ServerCapability.ViewServer,server,Onward)]));
        Check(r.Repo.Read().HostGrants.Count==0&&!r.Repo.Read().Policy.IsActive(ActorRef.RemoteManager(r.F.PeerId)));
        Check(r.Activate(r.F.PeerId)==PeerActivationDisposition.Activated);var s=r.Repo.Read();var peer=ActorRef.RemoteManager(r.F.PeerId);
        Check(s.HostGrants.Count==1&&s.ServerGrants.Count==1&&r.Applied==2);
        Check(!s.Policy.CanUseHost(peer,HostCapability.CreateServer,r.F.HostId)&&s.Policy.CanUseHost(peer,HostCapability.ManageHostUpdates,r.F.HostId));
        Check(!s.Policy.CanUseHost(peer,HostCapability.ManageHostUpdates,r.F.PeerId)&&s.Policy.CanUseServer(peer,ServerCapability.ViewServer,server));
        foreach(var grant in s.HostGrants.Cast<CapabilityGrant>().Concat(s.ServerGrants))Check(grant.DerivedFromGrantId is null&&grant.GrantedByActor==ActorRef.LocalPrincipal(r.F.OwnerId)&&grant.GranteeActor==peer);
        using(var cmd=r.F.Writer.CreateCommand())
        {
            cmd.CommandText="SELECT ActorKind,ActorLocalPrincipalId,ActorPeerHostId,Summary FROM AuditEvents WHERE EventKind='DefaultGrantApplied';";
            using var reader=cmd.ExecuteReader();int count=0;while(reader.Read())
            {Check(reader.GetString(0)=="RemoteManager"&&reader.IsDBNull(1)&&reader.GetString(2)==r.F.PeerId.ToString("D"));Check(reader.GetString(3).Contains(current.ConfigurationId!.Value.ToString("D"))&&reader.GetString(3).Contains(r.F.OwnerId.ToString("D"))&&!reader.GetString(3).Contains("fixture-public"));count++;}Check(count==2);
        }
        r.Configure(DefaultGrantTemplate.Factory);Check(r.Activate(r.F.PeerId)==PeerActivationDisposition.AlreadyActive&&r.Applied==2);
        var next=Guid.NewGuid();r.Bind(next);r.Activate(next);var after=r.Repo.Read();
        Check(after.HostGrants.SequenceEqual(s.HostGrants)&&after.ServerGrants.SequenceEqual(s.ServerGrants)&&r.Applied==2);
        Check(!after.Policy.CanUseHost(ActorRef.RemoteManager(next),HostCapability.ManageHostUpdates,r.F.HostId));
        return Task.CompletedTask;
    }
    public static Task ConfigurationAuditAndMutationRollback()
    {
        foreach(var mutation in new[]{"SELECT RAISE(ABORT,'fixture config audit');","UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;","DELETE FROM HostDefaultGrants;"})
        {
            using var r=new Rig();var prior=r.Configure(HostOnly(HostCapability.CreateServer));var audits=r.F.Count("AuditEvents");
            r.F.Execute($"CREATE TRIGGER DefaultConfigFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='DefaultGrantTemplateConfigured' BEGIN {mutation} END;");
            void Change()=>r.Configure(HostOnly(HostCapability.ManageHostUpdates));
            if(mutation.StartsWith("SELECT",StringComparison.Ordinal))Reject<SqliteException>(Change);
            else if(mutation.Contains("LocalPrincipals",StringComparison.Ordinal))Reject<AuthenticationException>(Change);
            else Reject<StaleAuthorizationRevisionException>(Change);
            var after=r.Repo.ReadDefaults();Check(after.ConfigurationId==prior.ConfigurationId&&after.Revision==prior.Revision&&after.Template.Hosts.SequenceEqual(prior.Template.Hosts));
            Check(r.F.Count("AuditEvents")==audits&&r.Repo.Read().HostGrants.Count==0);
        }
        return Task.CompletedTask;
    }
    public static Task InnerAndOuterActivationAuditRollback()
    {
        foreach(var eventKind in new[]{"DefaultGrantApplied","PeerActivated"})
        foreach(var mutation in new[]{"SELECT RAISE(ABORT,'fixture activation audit');","UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;","UPDATE HostDefaultGrants SET CanDelegate=CanDelegate;","DELETE FROM HostCapabilityGrants;"})
        {
            using var r=new Rig();r.Bind(r.F.PeerId);r.Configure(HostOnly(HostCapability.ManageHostUpdates));var before=r.Repo.Read().Revision;var audits=r.F.Count("AuditEvents");
            r.F.Execute($"CREATE TRIGGER DefaultActivationFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='{eventKind}' BEGIN {mutation} END;");
            if(mutation.StartsWith("SELECT",StringComparison.Ordinal))Reject<SqliteException>(()=>r.Activate(r.F.PeerId));
            else Reject<StaleAuthorizationRevisionException>(()=>r.Activate(r.F.PeerId));
            Check(r.F.Repository.Read(r.F.PeerId)!.State=="PeerBound"&&r.Repo.Read().Revision==before&&r.Repo.Read().HostGrants.Count==0&&r.F.Count("AuditEvents")==audits);
            r.F.Execute("DROP TRIGGER DefaultActivationFault;");Check(r.Activate(r.F.PeerId)==PeerActivationDisposition.Activated&&r.Applied==1);
        }
        return Task.CompletedTask;
    }
    private sealed class FinalClock(DateTimeOffset now,bool expire,CancellationTokenSource cancel):TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow(){if(++calls>1){cancel.Cancel();if(expire)return now.AddMinutes(31);}return now;}
    }
    private sealed class MissingGuard:IPeerActivationHook
    {public Action Apply(SqliteConnection c,SqliteTransaction tx,PeerActivationContext activation)=>null!;}
    private sealed class SlowFinalGuard(IPeerActivationHook actual,PeerTrustTests.Clock clock):IPeerActivationHook
    {
        public Action Apply(SqliteConnection c,SqliteTransaction tx,PeerActivationContext activation)
        {
            var validate=actual.Apply(c,tx,activation);
            return ()=>{validate();clock.Now+=TimeSpan.FromMinutes(31);};
        }
    }
    public static Task ExpiryCancellationAndMissingFinalGuard()
    {
        foreach(var expire in new[]{false,true})
        {
            using var r=new Rig();r.Bind(r.F.PeerId);r.Configure(HostOnly(HostCapability.CreateServer));var before=r.Repo.Read().Revision;var audits=r.F.Count("AuditEvents");
            using var cancel=new CancellationTokenSource();var repo=new PeerTrustRepository(r.F.Database,r.F.HostId,new FinalClock(r.F.Time.Now,expire,cancel));
            void Activate()=>repo.AcceptOwnerActivationAcknowledgement(r.Owner,r.F.PeerId,Peer,Local,new(r.F.PeerId,r.F.HostId,Local),r.Repo.CreateDefaultActivationHook(),cancel.Token);
            if(expire)Reject<InvalidOperationException>(Activate);else Reject<OperationCanceledException>(Activate);
            Check(r.F.Repository.Read(r.F.PeerId)!.State=="PeerBound"&&r.Repo.Read().Revision==before&&r.F.Count("AuditEvents")==audits&&r.Applied==0);
        }
        using(var r=new Rig())
        {r.Bind(r.F.PeerId);var before=r.Repo.Read().Revision;Reject<InvalidOperationException>(()=>r.Activate(r.F.PeerId,new MissingGuard()));Check(r.Repo.Read().Revision==before&&r.F.Repository.Read(r.F.PeerId)!.State=="PeerBound");}
        using(var r=new Rig())
        {
            r.Bind(r.F.PeerId);r.Configure(HostOnly(HostCapability.CreateServer));var before=r.Repo.Read().Revision;var audits=r.F.Count("AuditEvents");
            Reject<InvalidOperationException>(()=>r.Activate(r.F.PeerId,new SlowFinalGuard(r.Repo.CreateDefaultActivationHook(),r.F.Time)));
            Check(r.F.Repository.Read(r.F.PeerId)!.State=="PeerBound"&&r.Repo.Read().Revision==before&&r.F.Count("AuditEvents")==audits&&r.Applied==0);
        }
        return Task.CompletedTask;
    }
    public static Task ConcurrentActivationUsesIndependentGuards()
    {
        using var r=new Rig();var second=Guid.NewGuid();r.Bind(r.F.PeerId);r.Bind(second);r.Configure(HostOnly(HostCapability.CreateServer));
        var hook=r.Repo.CreateDefaultActivationHook();int activated=0,retry=0;
        Parallel.For(0,8,i=>{var result=r.Activate(i%2==0?r.F.PeerId:second,hook);if(result==PeerActivationDisposition.Activated)Interlocked.Increment(ref activated);else Interlocked.Increment(ref retry);});
        Check(activated==2&&retry==6&&r.Applied==2&&r.Repo.Read().HostGrants.Count==2);
        foreach(var peer in new[]{r.F.PeerId,second})Check(r.Repo.Read().HostGrants.Count(g=>g.GranteeActor==ActorRef.RemoteManager(peer))==1);
        return Task.CompletedTask;
    }
    public static Task TwoHostActivationIsIndependent()
    {
        using var a=new Rig();using var b=new Rig();b.F.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{Peer}' WHERE CredentialRef='current';");
        a.F.Repository.RecordVerifiedBinding(b.F.HostId,Peer,Local);b.F.Repository.RecordVerifiedBinding(a.F.HostId,Local,Peer);
        a.Configure(HostOnly(HostCapability.CreateServer));b.Configure(HostOnly(HostCapability.ManageHostUpdates));
        var ackA=a.F.Repository.PrepareActivationAcknowledgement(b.F.HostId,Peer,Local);var ackB=b.F.Repository.PrepareActivationAcknowledgement(a.F.HostId,Local,Peer);
        a.F.Repository.AcceptActivationAcknowledgement(b.F.HostId,Peer,Local,ackB,a.Repo.CreateDefaultActivationHook());
        b.F.Execute("CREATE TRIGGER FailIndependentActivation AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PeerActivated' BEGIN SELECT RAISE(ABORT,'fixture independent failure'); END;");
        Reject<SqliteException>(()=>b.F.Repository.AcceptActivationAcknowledgement(a.F.HostId,Local,Peer,ackA,b.Repo.CreateDefaultActivationHook()));
        Check(a.Applied==1&&b.Applied==0&&a.F.Repository.Read(b.F.HostId)!.State=="Active"&&b.F.Repository.Read(a.F.HostId)!.State=="PeerBound");
        b.F.Execute("DROP TRIGGER FailIndependentActivation;");b.Configure(DefaultGrantTemplate.Factory);
        b.F.Repository.AcceptActivationAcknowledgement(a.F.HostId,Local,Peer,ackA,b.Repo.CreateDefaultActivationHook());
        Check(a.Applied==1&&b.Applied==0&&b.Repo.Read().HostGrants.Count==0);
        return Task.CompletedTask;
    }
}
