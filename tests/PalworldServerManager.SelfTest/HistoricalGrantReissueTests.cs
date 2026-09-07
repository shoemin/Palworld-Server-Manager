using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static class HistoricalGrantReissueTests
{
    private static readonly DelegationRights Onward=new(true,true),Use=new(false,false);
    private static void Check(bool value){if(!value)throw new Exception("Historical reissue assertion failed.");}
    private static void Reject<T>(Action action)where T:Exception
    {try{action();}catch(T){return;}throw new Exception("Expected historical reissue refusal: "+typeof(T).Name);}
    private sealed class Rig:IDisposable
    {
        internal readonly PeerTrustTests.Fixture F=new();
        internal readonly Guid Local=Guid.NewGuid(),OtherPeer=Guid.NewGuid(),Root=Guid.NewGuid(),Child=Guid.NewGuid();
        internal readonly ServerRef Target;
        internal GrantPolicyRepository Repo=>new(F.Database,F.HostId,F.Time);
        internal LocalPrincipalMutationActor Owner=>new(F.HostId,F.OwnerId,"native-owner","fixture-public");
        internal LocalPrincipalMutationActor NonOwner=>new(F.HostId,Local,"native-local","public-local");
        internal long Revision=>Repo.Read().Revision;
        internal Rig()
        {
            Target=new(F.HostId,Guid.NewGuid());
            F.Execute($"INSERT INTO LocalPrincipals (LocalPrincipalId,OsPrincipalRef,PublicVerificationKey,IsOwner,State,CreatedUtc) VALUES ('{Local:D}','native-local','public-local',0,'Active','{F.Time.Now:O}');");
            foreach(var peer in new[]{F.PeerId,OtherPeer})F.Execute($"INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('{peer:D}','Active','{new string('B',64)}','{F.Time.Now:O}');");
            Repo.IssueHost(Owner,Revision,Root,ActorRef.RemoteManager(F.PeerId),HostCapability.CreateServer,F.HostId,Onward,null);
            Repo.IssueServer(Owner,Revision,Root,ActorRef.RemoteManager(F.PeerId),ServerCapability.ViewServer,Target,Use,null);
            // Explicit old delegated fixture, issued by the peer that held the old root.
            F.Execute($"INSERT INTO HostCapabilityGrants (GrantId,Capability,TargetHostId,GranteeActorKind,GranteeLocalPrincipalId,GrantedByActorKind,GrantedByPeerHostId,CanDelegate,CanDelegateOnwardDelegation,DerivedFromGrantId,CreatedUtc) VALUES ('{Child:D}','CreateServer','{F.HostId:D}','LocalPrincipal','{Local:D}','RemoteManager','{F.PeerId:D}',0,0,'{Root:D}','{F.Time.Now:O}');");
            Check(Repo.Read().Policy.CanUseHost(ActorRef.LocalPrincipal(Local),HostCapability.CreateServer,F.HostId));
            Repo.InvalidateHostSubtree(Owner,Revision,Root);Repo.InvalidateServerSubtree(Owner,Revision,Root);
        }
        internal HistoricalGrantReissueResult Apply(long? revision=null)=>Repo.ReissueHistoricalPeerRoots(Owner,revision??Revision,F.PeerId,[Root],[Root]);
        public void Dispose()=>F.Dispose();
    }
    public static Task PureRulesRequireOwnerAndHistoricalRoots()
    {
        using var r=new Rig();var policy=r.Repo.Read().Policy;var owner=ActorRef.LocalPrincipal(r.F.OwnerId);
        var id=Guid.NewGuid();var h=policy.ReissueHostRoot(owner,id,r.F.PeerId,r.Root,r.F.Time.Now);
        var s=policy.ReissueServerRoot(owner,id,r.F.PeerId,r.Root,r.F.Time.Now);
        Check(h.GrantId==id&&h.DerivedFromGrantId is null&&h.GrantedByActor==owner&&h.Rights==Onward&&h.TargetHostId==r.F.HostId);
        Check(s.Target==r.Target&&s.Rights==Use&&s.DerivedFromGrantId is null&&s.InvalidatedUtc is null);
        Check(!policy.CanUseHost(ActorRef.RemoteManager(r.F.PeerId),HostCapability.CreateServer,r.F.HostId));
        Reject<UnauthorizedAccessException>(()=>policy.ReissueHostRoot(ActorRef.RemoteManager(r.F.PeerId),Guid.NewGuid(),r.F.PeerId,r.Root,r.F.Time.Now));
        Reject<UnauthorizedAccessException>(()=>policy.ReissueHostRoot(owner,r.Root,r.F.PeerId,r.Root,r.F.Time.Now));
        Reject<UnauthorizedAccessException>(()=>policy.ReissueHostRoot(owner,Guid.NewGuid(),r.OtherPeer,r.Root,r.F.Time.Now));
        Reject<UnauthorizedAccessException>(()=>policy.ReissueHostRoot(owner,Guid.NewGuid(),r.F.PeerId,r.Child,r.F.Time.Now));
        Reject<UnauthorizedAccessException>(()=>policy.ReissueServerRoot(owner,Guid.NewGuid(),r.F.PeerId,r.Child,r.F.Time.Now));
        // Same-peer historical descendants isolate the no-parent predicate; the stored
        // descendant fixture above also differs by grantee and cannot prove this alone.
        var peer=ActorRef.RemoteManager(r.F.PeerId);var childId=Guid.NewGuid();var now=r.F.Time.Now;
        var oldHost=new HostCapabilityGrant(r.Root,peer,HostCapability.CreateServer,r.F.HostId,Onward,owner,null,now,now);
        var oldServer=new ServerCapabilityGrant(r.Root,peer,ServerCapability.ViewServer,r.Target,Onward,owner,null,now,now);
        var childHost=new HostCapabilityGrant(childId,peer,HostCapability.CreateServer,r.F.HostId,Use,peer,r.Root,now,now);
        var childServer=new ServerCapabilityGrant(childId,peer,ServerCapability.ViewServer,r.Target,Use,peer,r.Root,now,now);
        var historical=new AuthorizationPolicy(r.F.HostId,r.F.OwnerId,[r.F.OwnerId],[r.F.PeerId],[oldHost,childHost],[oldServer,childServer]);
        Reject<UnauthorizedAccessException>(()=>historical.ReissueHostRoot(owner,Guid.NewGuid(),r.F.PeerId,childId,now));
        Reject<UnauthorizedAccessException>(()=>historical.ReissueServerRoot(owner,Guid.NewGuid(),r.F.PeerId,childId,now));
        return Task.CompletedTask;
    }
    public static Task NewRootsPreserveHistoryAndActualAudit()
    {
        using var r=new Rig();var before=r.Repo.Read();var result=r.Apply();var after=r.Repo.Read();
        var batch=result.AuditBatchId??throw new Exception("Missing reissue audit batch.");
        Check(result.Revision==before.Revision+2&&result.Hosts.Single().HistoricalGrantId==r.Root&&result.Servers.Single().HistoricalGrantId==r.Root);
        var h=after.HostGrants.Single(g=>g.GrantId==result.Hosts[0].NewGrantId);var s=after.ServerGrants.Single(g=>g.GrantId==result.Servers[0].NewGrantId);
        Check(h.GrantId!=r.Root&&s.GrantId!=r.Root&&h.GranteeActor==ActorRef.RemoteManager(r.F.PeerId)&&s.GranteeActor==h.GranteeActor);
        Check(h.DerivedFromGrantId is null&&s.DerivedFromGrantId is null&&h.GrantedByActor==ActorRef.LocalPrincipal(r.F.OwnerId));
        Check(after.Policy.CanUseHost(h.GranteeActor,HostCapability.CreateServer,r.F.HostId)&&after.Policy.CanUseServer(h.GranteeActor,ServerCapability.ViewServer,r.Target));
        Check(!after.Policy.CanUseHost(ActorRef.LocalPrincipal(r.Local),HostCapability.CreateServer,r.F.HostId));
        Check(!after.Policy.CanUseServer(s.GranteeActor,ServerCapability.ViewServer,new(r.OtherPeer,r.Target.ServerProfileId)));
        foreach(var old in before.HostGrants)Check(after.HostGrants.Single(g=>g.GrantId==old.GrantId)==old);
        foreach(var old in before.ServerGrants)Check(after.ServerGrants.Single(g=>g.GrantId==old.GrantId)==old);
        using var cmd=r.F.Writer.CreateCommand();cmd.CommandText="SELECT ActorKind,ActorLocalPrincipalId,ActorPeerHostId,Summary FROM AuditEvents WHERE EventKind='CapabilityGrantReissued';";
        using var reader=cmd.ExecuteReader();int count=0;
        while(reader.Read())
        {
            var summary=reader.GetString(3);
            Check(reader.GetString(0)=="LocalPrincipal"&&reader.GetString(1)==r.F.OwnerId.ToString("D")&&reader.IsDBNull(2));
            Check(summary.Contains("Source=OwnerRoot")&&summary.Contains("HistoricalGrant="+r.Root.ToString("D"))&&summary.Contains(batch.ToString("D"))&&!summary.Contains("fixture-public")&&!summary.Contains("native-owner"));count++;
        }
        Check(count==2);Reject<NotSupportedException>(()=>((IList<ReissuedGrant>)result.Hosts).Clear());
        return Task.CompletedTask;
    }
    public static Task InvalidSelectionsAndNonOwnerHaveNoEffects()
    {
        using var r=new Rig();r.Repo.IssueHost(r.Owner,r.Revision,Guid.NewGuid(),ActorRef.LocalPrincipal(r.Local),HostCapability.ManagePermissions,r.F.HostId,Onward,null);
        var active=Guid.NewGuid();r.Repo.IssueHost(r.Owner,r.Revision,active,ActorRef.RemoteManager(r.F.PeerId),HostCapability.ManageHostUpdates,r.F.HostId,Onward,null);
        var before=r.Revision;var audits=r.F.Count("AuditEvents");var grants=r.F.Count("HostCapabilityGrants");
        Reject<UnauthorizedAccessException>(()=>r.Repo.ReissueHistoricalPeerRoots(r.NonOwner,before,r.F.PeerId,[r.Root],[r.Root]));
        Reject<AuthenticationException>(()=>r.Repo.ReissueHistoricalPeerRoots(r.Owner with {PublicVerificationKey="old"},before,r.F.PeerId,[r.Root],[]));
        foreach(var bad in new[]{Guid.NewGuid(),r.Child,active})
            Reject<UnauthorizedAccessException>(()=>r.Repo.ReissueHistoricalPeerRoots(r.Owner,before,r.F.PeerId,[r.Root,bad],[]));
        Reject<UnauthorizedAccessException>(()=>r.Repo.ReissueHistoricalPeerRoots(r.Owner,before,r.OtherPeer,[r.Root],[]));
        Reject<ArgumentException>(()=>r.Repo.ReissueHistoricalPeerRoots(r.Owner,before,r.F.PeerId,[r.Root,r.Root],[]));
        Reject<ArgumentException>(()=>r.Repo.ReissueHistoricalPeerRoots(r.Owner,before,r.F.PeerId,[],[Guid.Empty]));
        Reject<StaleAuthorizationRevisionException>(()=>r.Apply(before-1));
        Check(r.Revision==before&&r.F.Count("AuditEvents")==audits&&r.F.Count("HostCapabilityGrants")==grants&&r.F.Count("ServerCapabilityGrants")==1);
        return Task.CompletedTask;
    }
    public static Task CurrentPeerStateIsRequired()
    {
        foreach(var state in new[]{"PeerBound","Revoked","Recovery"})
        {
            using var r=new Rig();r.F.Execute(state=="Recovery"?$"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{r.F.PeerId:D}';":$"UPDATE TrustedManagers SET State='{state}',CurrentTrustedPublicKeyFingerprint={(state=="Revoked"?"NULL":"CurrentTrustedPublicKeyFingerprint")} WHERE PeerHostId='{r.F.PeerId:D}';");
            var before=r.Revision;var audits=r.F.Count("AuditEvents");
            Reject<UnauthorizedAccessException>(()=>r.Apply());
            Reject<UnauthorizedAccessException>(()=>r.Repo.ReissueHistoricalPeerRoots(r.Owner,before,r.F.PeerId,[],[]));
            Check(r.Revision==before&&r.F.Count("AuditEvents")==audits);
        }
        return Task.CompletedTask;
    }
    public static Task LaterAuditAndAuthorityMutationRollBack()
    {
        foreach(var mode in new[]{0,1,2,3})
        {
            using var r=new Rig();var before=r.Revision;var audits=r.F.Count("AuditEvents");
            var mutation=mode switch
            {
                0=>"SELECT RAISE(ABORT,'second reissue audit fault');",
                1=>$"UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE LocalPrincipalId='{r.F.OwnerId:D}';",
                2=>$"UPDATE HostCapabilityGrants SET InvalidatedUtc=NULL WHERE GrantId='{r.Root:D}';",
                _=>$"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{r.F.PeerId:D}';"
            };
            r.F.Execute($"CREATE TRIGGER ReissueFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='CapabilityGrantReissued' AND NEW.AffectedServerProfileId IS NOT NULL BEGIN {mutation} END;");
            if(mode==0)Reject<SqliteException>(()=>r.Apply());else if(mode==1)Reject<AuthenticationException>(()=>r.Apply());else Reject<StaleAuthorizationRevisionException>(()=>r.Apply());
            Check(r.Revision==before&&r.F.Count("AuditEvents")==audits&&r.F.Count("HostCapabilityGrants")==2&&r.F.Count("ServerCapabilityGrants")==1);
            Check(r.Repo.Read().HostGrants.All(g=>g.InvalidatedUtc is not null));
            r.F.Execute("DROP TRIGGER ReissueFault;");Check(r.Apply().Hosts.Count==1);
        }
        return Task.CompletedTask;
    }
    public static Task ConcurrentSelectionAndFreshExplicitRepeat()
    {
        using var r=new Rig();var before=r.Revision;int won=0,stale=0;
        Parallel.For(0,8,_=>{try{r.Apply(before);Interlocked.Increment(ref won);}catch(StaleAuthorizationRevisionException){Interlocked.Increment(ref stale);}});
        Check(won==1&&stale==7&&r.Revision==before+2);
        // A newly authorized ordinary Owner action can deliberately issue another root;
        // a lost-response retry with the old revision cannot silently duplicate it.
        var first=r.Repo.Read();var second=r.Apply();
        Check(second.Revision==before+4&&second.Hosts.All(x=>first.HostGrants.All(g=>g.GrantId!=x.NewGrantId)));
        Check(r.Repo.Read().HostGrants.Single(g=>g.GrantId==r.Child).InvalidatedUtc is not null);
        return Task.CompletedTask;
    }
    private sealed class CancelAtWrite(TimeProvider actual,CancellationTokenSource cancellation):TimeProvider
    {public override DateTimeOffset GetUtcNow(){cancellation.Cancel();return actual.GetUtcNow();}}
    public static Task EmptyAndCancelledCallsDoNotWrite()
    {
        using var r=new Rig();var before=r.Revision;var audits=r.F.Count("AuditEvents");
        var result=r.Repo.ReissueHistoricalPeerRoots(r.Owner,before,r.F.PeerId,[],[]);
        Check(result.AuditBatchId is null&&result.Revision==before&&result.Hosts.Count==0&&result.Servers.Count==0);
        Reject<UnauthorizedAccessException>(()=>r.Repo.ReissueHistoricalPeerRoots(r.NonOwner,before,r.F.PeerId,[],[]));
        Reject<StaleAuthorizationRevisionException>(()=>r.Repo.ReissueHistoricalPeerRoots(r.Owner,before-1,r.F.PeerId,[],[]));
        using var ct=new CancellationTokenSource();var repo=new GrantPolicyRepository(r.F.Database,r.F.HostId,new CancelAtWrite(r.F.Time,ct));
        Reject<OperationCanceledException>(()=>repo.ReissueHistoricalPeerRoots(r.Owner,before,r.F.PeerId,[r.Root],[r.Root],ct.Token));
        Check(r.Revision==before&&r.F.Count("AuditEvents")==audits);
        return Task.CompletedTask;
    }
}
