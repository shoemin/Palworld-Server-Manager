using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static class RolePresetTests
{
    private static readonly DelegationRights Use=new(false,false),Delegate=new(true,false),Onward=new(true,true);
    private static void Check(bool value){if(!value)throw new Exception("Role preset assertion failed.");}
    private static void Reject<T>(Action action)where T:Exception
    {try{action();}catch(T){return;}throw new Exception("Expected preset refusal: "+typeof(T).Name);}
    private sealed class Rig:IDisposable
    {
        internal readonly PeerTrustTests.Fixture F=new();
        internal readonly Guid A=Guid.NewGuid(),B=Guid.NewGuid();
        internal GrantPolicyRepository Repo=>new(F.Database,F.HostId,F.Time);
        internal LocalPrincipalMutationActor Owner=>new(F.HostId,F.OwnerId,"native-owner","fixture-public");
        internal LocalPrincipalMutationActor Applier=>new(F.HostId,A,"native-a","public-a");
        internal ActorRef Grantee=>ActorRef.LocalPrincipal(B);
        internal long Revision=>Repo.Read().Revision;
        internal Rig()=>F.Execute($"INSERT INTO LocalPrincipals (LocalPrincipalId,OsPrincipalRef,PublicVerificationKey,IsOwner,State,CreatedUtc) VALUES ('{A:D}','native-a','public-a',0,'Active','{F.Time.Now:O}'),('{B:D}','native-b','public-b',0,'Active','{F.Time.Now:O}');");
        internal Guid Root(HostCapability cap=HostCapability.CreateServer,DelegationRights? rights=null)
        {var id=Guid.NewGuid();Repo.IssueHost(Owner,Revision,id,ActorRef.LocalPrincipal(A),cap,F.HostId,rights??Onward,null);return id;}
        internal HostGrantRequest Host(Guid? source=null,HostCapability cap=HostCapability.CreateServer,Guid? target=null,DelegationRights? rights=null,Guid? id=null)
            =>new(id??Guid.NewGuid(),Grantee,cap,target??F.HostId,rights??Use,source);
        internal RolePreset OwnerPreset()=>new("presentation-only",[Host(),Host(cap:HostCapability.ManageHostUpdates)],[]);
        public void Dispose()=>F.Dispose();
    }
    public static Task ImmutableShapesAndPureCanonicalExpansion()
    {
        var host=Guid.NewGuid();var owner=ActorRef.LocalPrincipal(Guid.NewGuid());var a=ActorRef.LocalPrincipal(Guid.NewGuid());var b=ActorRef.LocalPrincipal(Guid.NewGuid());
        var policy=new AuthorizationPolicy(host,owner.Id,[owner.Id,a.Id,b.Id],[],[],[]);var id=Guid.NewGuid();
        var hs=new List<HostGrantRequest>{new(id,b,HostCapability.CreateServer,host,Use,Guid.NewGuid())};
        var ss=new List<ServerGrantRequest>{new(id,b,ServerCapability.ViewServer,new(host,Guid.NewGuid()),Use,Guid.NewGuid())};
        var preset=new RolePreset("View Only",hs,ss);hs.Clear();ss.Clear();Check(preset.Hosts.Count==1&&preset.Servers.Count==1);
        Reject<NotSupportedException>(()=>((IList<HostGrantRequest>)preset.Hosts).Clear());
        var roots=policy.ExpandPreset(owner,preset,DateTimeOffset.UtcNow);
        Check(roots.Hosts.Single().DerivedFromGrantId is null&&roots.Servers.Single().DerivedFromGrantId is null);
        Check(roots.Hosts.Single().GrantedByActor==owner&&roots.Servers.Single().GrantedByActor==owner);
        Check(!policy.CanUseHost(b,HostCapability.CreateServer,host)); // expansion has no write effect
        Reject<NotSupportedException>(()=>((IList<HostCapabilityGrant>)roots.Hosts).Clear());
        Reject<UnauthorizedAccessException>(()=>policy.ExpandPreset(a,preset,DateTimeOffset.UtcNow));
        Reject<ArgumentException>(()=>new RolePreset("duplicate",[preset.Hosts[0],preset.Hosts[0]],[]));
        Reject<ArgumentException>(()=>new HostGrantRequest(Guid.NewGuid(),b,(HostCapability)99,host,Use));
        Reject<ArgumentException>(()=>new ServerGrantRequest(Guid.NewGuid(),b,(ServerCapability)0,new(host,Guid.NewGuid()),Use));
        Reject<ArgumentException>(()=>new RolePreset("bad\nlabel",[],[]));
        var parent=policy.IssueHost(owner,Guid.NewGuid(),a,HostCapability.CreateServer,host,Onward,null,DateTimeOffset.UtcNow);
        var withParent=new AuthorizationPolicy(host,owner.Id,[owner.Id,a.Id,b.Id],[],[parent],[]);var intermediate=Guid.NewGuid();
        var chained=new RolePreset("cannot bootstrap",[new(intermediate,a,HostCapability.CreateServer,host,Onward,parent.GrantId),new(Guid.NewGuid(),b,HostCapability.CreateServer,host,Use,intermediate)],[]);
        Reject<UnauthorizedAccessException>(()=>withParent.ExpandPreset(a,chained,DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }
    public static Task MixedPresetPersistsExactRowsAndAudit()
    {
        using var r=new Rig();var root=r.Root();var target=new ServerRef(r.F.PeerId,Guid.NewGuid());var sr=Guid.NewGuid();
        r.Repo.IssueServer(r.Owner,r.Revision,sr,ActorRef.LocalPrincipal(r.A),ServerCapability.ViewServer,target,Onward,null);
        var id=Guid.NewGuid();var preset=new RolePreset("private-label-not-for-audit",[r.Host(root,id:id)],[new(id,r.Grantee,ServerCapability.ViewServer,target,Use,sr)]);
        var before=r.Revision;var result=r.Repo.ApplyPreset(r.Applier,before,preset);var state=r.Repo.Read();
        var batchId=result.AuditBatchId??throw new Exception("Expected a committed preset audit batch.");
        Check(result.Revision==before+2&&result.HostGrantIds.SequenceEqual([id])&&result.ServerGrantIds.SequenceEqual([id]));
        Check(state.HostGrants.Single(g=>g.GrantId==id).DerivedFromGrantId==root&&state.ServerGrants.Single(g=>g.GrantId==id).DerivedFromGrantId==sr);
        Check(state.Policy.CanUseHost(r.Grantee,HostCapability.CreateServer,r.F.HostId)&&state.Policy.CanUseServer(r.Grantee,ServerCapability.ViewServer,target));
        Check(!state.Policy.CanUseServer(r.Grantee,ServerCapability.ViewServer,new(r.F.HostId,target.ServerProfileId)));
        using var cmd=r.F.Writer.CreateCommand();cmd.CommandText="SELECT ActorLocalPrincipalId,Summary FROM AuditEvents WHERE Summary LIKE '%RolePreset:%';";
        using var reader=cmd.ExecuteReader();int count=0;while(reader.Read())
        {Check(reader.GetString(0)==r.A.ToString("D")&&reader.GetString(1).Contains(batchId.ToString("D"))&&!reader.GetString(1).Contains(preset.Label)&&!reader.GetString(1).Contains("public-a"));count++;}Check(count==2);
        Reject<NotSupportedException>(()=>((IList<Guid>)result.HostGrantIds).Clear());
        return Task.CompletedTask;
    }
    public static Task AnyUnauthorizedEntryRejectsWholePreset()
    {
        using var r=new Rig();var root=r.Root(rights:Delegate);var permissions=r.Root(HostCapability.ManagePermissions);var before=r.Revision;var audits=r.F.Count("AuditEvents");
        var invalids=new[]{r.Host(root,target:r.F.PeerId),r.Host(root,cap:HostCapability.ManageHostUpdates),r.Host(root,rights:Onward),r.Host(permissions),r.Host()};
        foreach(var invalid in invalids)Reject<UnauthorizedAccessException>(()=>r.Repo.ApplyPreset(r.Applier,before,new("Host Administrator",[r.Host(root),invalid],[])));
        var missingServer=new ServerGrantRequest(Guid.NewGuid(),r.Grantee,ServerCapability.ViewServer,new(r.F.HostId,Guid.NewGuid()),Use,root);
        Reject<UnauthorizedAccessException>(()=>r.Repo.ApplyPreset(r.Applier,before,new("mixed invalid",[r.Host(root)],[missingServer])));
        Reject<AuthenticationException>(()=>r.Repo.ApplyPreset(r.Applier with {PublicVerificationKey="stale"},before,new("stale proof",[r.Host(root)],[])));
        Check(r.Revision==before&&r.F.Count("AuditEvents")==audits&&r.Repo.Read().HostGrants.Count==2&&r.Repo.Read().ServerGrants.Count==0);
        return Task.CompletedTask;
    }
    public static Task AuditAndSourceChangesRollBackWholeBatch()
    {
        foreach(var mode in new[]{0,1,2})
        {
            using var r=new Rig();var root=r.Root();var first=r.Host(root);var second=r.Host(root);var before=r.Revision;var audits=r.F.Count("AuditEvents");
            var mutation=mode switch {0=>"SELECT RAISE(ABORT,'fixture second-entry audit');",1=>$"UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE LocalPrincipalId='{r.A:D}';",_=>$"UPDATE HostCapabilityGrants SET InvalidatedUtc='{r.F.Time.Now:O}' WHERE GrantId='{root:D}';"};
            r.F.Execute($"CREATE TRIGGER PresetFault AFTER INSERT ON AuditEvents WHEN NEW.Summary LIKE '%{second.GrantId:D}%' BEGIN {mutation} END;");
            void Apply()=>r.Repo.ApplyPreset(r.Applier,before,new("atomic",[first,second],[]));
            if(mode==0)Reject<SqliteException>(Apply);else if(mode==1)Reject<AuthenticationException>(Apply);else Reject<StaleAuthorizationRevisionException>(Apply);
            Check(r.Revision==before&&r.F.Count("AuditEvents")==audits&&r.Repo.Read().HostGrants.Single().GrantId==root&&r.Repo.Read().HostGrants.Single().InvalidatedUtc is null);
            r.F.Execute("DROP TRIGGER PresetFault;");Check(r.Repo.ApplyPreset(r.Applier,before,new("retry",[first,second],[])).HostGrantIds.Count==2);
        }
        return Task.CompletedTask;
    }
    public static Task ConcurrentPresetsOwnerRootsAndExactRevocation()
    {
        using var r=new Rig();var before=r.Revision;int won=0,stale=0;
        Parallel.For(0,8,_=>{try{r.Repo.ApplyPreset(r.Owner,before,r.OwnerPreset());Interlocked.Increment(ref won);}catch(StaleAuthorizationRevisionException){Interlocked.Increment(ref stale);}});
        Check(won==1&&stale==7&&r.Revision==before+2&&r.Repo.Read().HostGrants.Count==2);
        var snapshot=r.Repo.Read();Check(snapshot.HostGrants.All(g=>g.DerivedFromGrantId is null&&g.GrantedByActor==ActorRef.LocalPrincipal(r.F.OwnerId)));
        r.Repo.InvalidateHostSubtree(r.Owner,r.Revision,snapshot.HostGrants[0].GrantId);
        Check(r.Repo.Read().HostGrants.Count(g=>g.InvalidatedUtc is not null)==1&&r.Repo.Read().Policy.IsOwner(ActorRef.LocalPrincipal(r.F.OwnerId)));
        Reject<StaleAuthorizationRevisionException>(()=>r.Repo.ApplyPreset(r.Owner,before,r.OwnerPreset()));
        return Task.CompletedTask;
    }
    private sealed class CancelAtWrite(TimeProvider actual,CancellationTokenSource cancellation):TimeProvider
    {public override DateTimeOffset GetUtcNow(){cancellation.Cancel();return actual.GetUtcNow();}}
    public static Task EmptyAndCancelledPresetHaveNoEffects()
    {
        using var r=new Rig();var before=r.Revision;var audits=r.F.Count("AuditEvents");var empty=new RolePreset("empty",[],[]);
        var noOp=r.Repo.ApplyPreset(r.Applier,before,empty);Check(noOp.AuditBatchId is null&&noOp.Revision==before&&noOp.HostGrantIds.Count==0&&noOp.ServerGrantIds.Count==0);
        Reject<StaleAuthorizationRevisionException>(()=>r.Repo.ApplyPreset(r.Owner,before-1,empty));
        using var cancellation=new CancellationTokenSource();var repo=new GrantPolicyRepository(r.F.Database,r.F.HostId,new CancelAtWrite(r.F.Time,cancellation));
        Reject<OperationCanceledException>(()=>repo.ApplyPreset(r.Owner,before,r.OwnerPreset(),cancellation.Token));
        Check(r.Revision==before&&r.F.Count("AuditEvents")==audits&&r.Repo.Read().HostGrants.Count==0);
        return Task.CompletedTask;
    }
}
