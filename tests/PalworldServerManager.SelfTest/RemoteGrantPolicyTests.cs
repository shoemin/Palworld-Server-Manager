using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static class RemoteGrantPolicyTests
{
    private static readonly DelegationRights Use=new(false,false),Delegate=new(true,false),Onward=new(true,true);
    private static void Check(bool value){if(!value)throw new Exception("Remote grant assertion failed.");}
    private static void Reject<T>(Action action)where T:Exception
    {try{action();}catch(T){return;}throw new Exception("Expected remote refusal: "+typeof(T).Name);}
    private sealed class Rig:IDisposable
    {
        internal readonly PeerTrustTests.Fixture F=new();
        internal readonly Guid Other=Guid.NewGuid(),HostRoot=Guid.NewGuid(),ServerRoot=Guid.NewGuid();
        internal readonly ServerRef Target;
        internal GrantPolicyRepository Repo=>new(F.Database,F.HostId,F.Time);
        internal LocalPrincipalMutationActor Owner=>new(F.HostId,F.OwnerId,"native-owner","fixture-public");
        internal ActorRef Peer=>ActorRef.RemoteManager(F.PeerId);
        internal ActorRef Grantee=>ActorRef.RemoteManager(Other);
        internal long Revision=>Repo.Read().Revision;
        internal PeerGrantMutationActor Actor=>new(F.HostId,F.PeerId,new('B',64),new('A',64),HostDatabase.QueryScalarLong(F.Writer,$"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{F.PeerId:D}';"));
        internal Rig(bool roots=true,DelegationRights? rights=null)
        {
            Target=new(F.HostId,Guid.NewGuid());
            foreach(var peer in new[]{F.PeerId,Other})F.Execute($"INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('{peer:D}','Active','{new string('B',64)}','{F.Time.Now:O}');");
            if(roots)
            {
                Repo.IssueHost(Owner,Revision,HostRoot,Peer,HostCapability.CreateServer,F.HostId,rights??Onward,null);
                Repo.IssueServer(Owner,Revision,ServerRoot,Peer,ServerCapability.ViewServer,Target,rights??Onward,null);
            }
        }
        internal RolePreset Preset()=>new("private-fixture-label",[new(Guid.NewGuid(),Grantee,HostCapability.CreateServer,F.HostId,Use,HostRoot)],
            [new(Guid.NewGuid(),Grantee,ServerCapability.ViewServer,Target,Use,ServerRoot)]);
        internal GrantMutationResult Host(PeerGrantMutationActor? actor=null,long? revision=null)
            =>Repo.IssueRemoteHost(actor??Actor,revision??Revision,Guid.NewGuid(),Grantee,HostCapability.CreateServer,F.HostId,Use,HostRoot);
        internal GrantMutationResult Server(PeerGrantMutationActor? actor=null,long? revision=null)
            =>Repo.IssueRemoteServer(actor??Actor,revision??Revision,Guid.NewGuid(),Grantee,ServerCapability.ViewServer,Target,Use,ServerRoot);
        public void Dispose()=>F.Dispose();
    }
    public static Task ExactDelegationPersistsRealPeerAndProvenance()
    {
        using var r=new Rig();var before=r.Revision;var host=r.Host();var server=r.Server();var snapshot=r.Repo.Read();
        Check(host.Revision==before+1&&server.Revision==before+2);
        var h=snapshot.HostGrants.Single(g=>g.GrantId==host.GrantId);var s=snapshot.ServerGrants.Single(g=>g.GrantId==server.GrantId);
        Check(h.GrantedByActor==r.Peer&&h.DerivedFromGrantId==r.HostRoot&&h.TargetHostId==r.F.HostId&&h.Rights==Use);
        Check(s.GrantedByActor==r.Peer&&s.DerivedFromGrantId==r.ServerRoot&&s.Target==r.Target&&s.Rights==Use);
        Check(snapshot.Policy.CanUseHost(r.Grantee,HostCapability.CreateServer,r.F.HostId)&&snapshot.Policy.CanUseServer(r.Grantee,ServerCapability.ViewServer,r.Target));
        using var cmd=r.F.Writer.CreateCommand();cmd.CommandText="SELECT ActorLocalPrincipalId,ActorPeerHostId,AffectedHostId,Summary FROM AuditEvents WHERE ActorKind='RemoteManager';";
        using var reader=cmd.ExecuteReader();int n=0;
        while(reader.Read())
        {
            var summary=reader.GetString(3);Check(reader.IsDBNull(0)&&reader.GetString(1)==r.F.PeerId.ToString("D")&&reader.GetString(2)==r.F.HostId.ToString("D"));
            Check(summary.Contains("Issuer=RemoteManager:"+r.F.PeerId.ToString("D"))&&!summary.Contains("OwnerRoot")&&summary.Contains(summary.Contains("Capability=CreateServer;")?r.HostRoot.ToString("D"):r.ServerRoot.ToString("D")));
            Check(!summary.Contains("fixture-public")&&!summary.Contains(new string('B',64)));n++;
        }
        Check(n==2);return Task.CompletedTask;
    }
    public static Task RootsScopeAndRightsCannotBeManufactured()
    {
        foreach(var rights in new[]{Use,Delegate,Onward})
        {
            using var r=new Rig(rights:rights);var before=r.Revision;
            Reject<UnauthorizedAccessException>(()=>r.Repo.IssueRemoteHost(r.Actor,r.Revision,Guid.NewGuid(),r.Grantee,HostCapability.CreateServer,r.F.HostId,Use,null));
            Reject<UnauthorizedAccessException>(()=>r.Repo.IssueRemoteServer(r.Actor,r.Revision,Guid.NewGuid(),r.Grantee,ServerCapability.ViewServer,r.Target,Use,null));
            Reject<UnauthorizedAccessException>(()=>r.Repo.IssueRemoteHost(r.Actor,r.Revision,Guid.NewGuid(),r.Grantee,HostCapability.ManageHostUpdates,r.F.HostId,Use,r.HostRoot));
            Reject<UnauthorizedAccessException>(()=>r.Repo.IssueRemoteServer(r.Actor,r.Revision,Guid.NewGuid(),r.Grantee,ServerCapability.ViewServer,r.Target,Use,r.HostRoot));
            if(!rights.CanDelegate){Reject<UnauthorizedAccessException>(()=>r.Host());Reject<UnauthorizedAccessException>(()=>r.Server());}
            if(!rights.CanDelegateOnwardDelegation)
                Reject<UnauthorizedAccessException>(()=>r.Repo.IssueRemoteHost(r.Actor,r.Revision,Guid.NewGuid(),r.Grantee,HostCapability.CreateServer,r.F.HostId,Delegate,r.HostRoot));
            Check(r.Revision==before);
            if(rights.CanDelegateOnwardDelegation)
                Check(r.Repo.IssueRemoteServer(r.Actor,r.Revision,Guid.NewGuid(),r.Grantee,ServerCapability.ViewServer,r.Target,Onward,r.ServerRoot).ChangedGrants==1);
        }
        using(var r=new Rig(false))
        {
            r.Repo.IssueHost(r.Owner,r.Revision,r.HostRoot,r.Peer,HostCapability.ManagePermissions,r.F.HostId,Onward,null);
            Reject<UnauthorizedAccessException>(()=>r.Host());
            var source=Guid.NewGuid();r.Repo.IssueHost(r.Owner,r.Revision,source,r.Grantee,HostCapability.CreateServer,r.F.HostId,Onward,null);
            Reject<UnauthorizedAccessException>(()=>r.Repo.IssueRemoteHost(r.Actor,r.Revision,Guid.NewGuid(),r.Grantee,HostCapability.CreateServer,r.F.HostId,Use,source));
        }
        using(var r=new Rig())
        {
            var h=Guid.NewGuid();var s=Guid.NewGuid();var elsewhere=new ServerRef(r.Other,r.Target.ServerProfileId);
            r.Repo.IssueHost(r.Owner,r.Revision,h,r.Peer,HostCapability.CreateServer,r.Other,Onward,null);
            r.Repo.IssueServer(r.Owner,r.Revision,s,r.Peer,ServerCapability.ViewServer,elsewhere,Onward,null);
            var before=r.Revision;
            Reject<UnauthorizedAccessException>(()=>r.Repo.IssueRemoteHost(r.Actor,before,Guid.NewGuid(),r.Grantee,HostCapability.CreateServer,r.Other,Use,h));
            Reject<UnauthorizedAccessException>(()=>r.Repo.IssueRemoteServer(r.Actor,before,Guid.NewGuid(),r.Grantee,ServerCapability.ViewServer,elsewhere,Use,s));
            Reject<UnauthorizedAccessException>(()=>r.Repo.IssueRemoteServer(r.Actor,before,Guid.NewGuid(),r.Grantee,ServerCapability.ViewServer,elsewhere,Use,r.ServerRoot));
            Check(r.Revision==before);
        }
        return Task.CompletedTask;
    }
    public static Task AllEntryPointsRequireCurrentTransportAndRevision()
    {
        using var r=new Rig();var actor=r.Actor;var before=r.Revision;var audits=r.F.Count("AuditEvents");
        Action<PeerGrantMutationActor,long>[] writers=[(a,v)=>r.Host(a,v),(a,v)=>r.Server(a,v),(a,v)=>r.Repo.ApplyRemotePreset(a,v,r.Preset())];
        foreach(var write in writers)
        {
            foreach(var bad in new[]{actor with{HostId=Guid.NewGuid()},actor with{PeerHostId=Guid.NewGuid()},actor with{PeerFingerprint=new('C',64)},actor with{LocalFingerprint=new('D',64)},actor with{Incarnation=actor.Incarnation+1}})
                Reject<AuthenticationException>(()=>write(bad,before));
            Reject<StaleAuthorizationRevisionException>(()=>write(actor,before-1));
        }
        Check(r.Revision==before&&r.F.Count("AuditEvents")==audits);
        foreach(var state in new[]{"PeerBound","Revoked","Recovery"})
        {
            using var x=new Rig();var proof=x.Actor;
            x.F.Execute(state=="Recovery"?$"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{x.F.PeerId:D}';":
                $"UPDATE TrustedManagers SET State='{state}'{(state=="Revoked"?",CurrentTrustedPublicKeyFingerprint=NULL":"")} WHERE PeerHostId='{x.F.PeerId:D}';");
            Reject<AuthenticationException>(()=>x.Host(proof));Reject<AuthenticationException>(()=>x.Server(proof));
            Reject<AuthenticationException>(()=>x.Repo.ApplyRemotePreset(proof,x.Revision,x.Preset()));
        }
        return Task.CompletedTask;
    }
    public static Task LateAuditMutationsRollBackSingleAndPresetWrites()
    {
        foreach(var preset in new[]{false,true})foreach(var mode in Enumerable.Range(0,8))
        {
            using var r=new Rig();var actor=r.Actor;var before=r.Revision;var audits=r.F.Count("AuditEvents");
            var sql=mode switch
            {
                0=>"SELECT RAISE(ABORT,'remote audit failed');",
                1=>$"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{r.F.PeerId:D}';",
                2=>$"DELETE FROM PeerRelationshipIncarnations WHERE PeerHostId='{r.F.PeerId:D}'; INSERT INTO PeerRelationshipIncarnations (PeerHostId) VALUES ('{r.F.PeerId:D}');",
                3=>$"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{new string('C',64)}' WHERE CredentialRef='current';",
                4=>$"UPDATE HostCapabilityGrants SET InvalidatedUtc='{r.F.Time.Now:O}' WHERE GrantId='{r.HostRoot:D}';",
                5=>$"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{r.Other:D}';",
                6=>$"UPDATE LocalPrincipals SET PublicVerificationKey='changed-owner-key' WHERE LocalPrincipalId='{r.F.OwnerId:D}';",
                _=>"UPDATE HostCapabilityGrants SET Capability='ManagePermissions' WHERE DerivedFromGrantId IS NOT NULL;"
            };
            var last=preset?"ViewServer":"CreateServer";
            r.F.Execute($"CREATE TRIGGER RemoteFault AFTER INSERT ON AuditEvents WHEN NEW.ActorKind='RemoteManager' AND NEW.Summary LIKE '%Capability={last};%' BEGIN {sql} END;");
            Action apply=()=>{if(preset)r.Repo.ApplyRemotePreset(actor,before,r.Preset());else r.Host(actor,before);};
            if(mode==0)Reject<SqliteException>(apply);else if(mode is 1 or 2 or 3)Reject<AuthenticationException>(apply);else Reject<StaleAuthorizationRevisionException>(apply);
            Check(r.Revision==before&&r.F.Count("HostCapabilityGrants")==1&&r.F.Count("ServerCapabilityGrants")==1&&r.F.Count("AuditEvents")==audits&&r.Actor==actor);
            r.F.Execute("DROP TRIGGER RemoteFault;");apply();Check(r.Revision==before+(preset?2:1));
        }
        return Task.CompletedTask;
    }
    public static Task PresetValidatesEveryOriginalSourceBeforeWriting()
    {
        using var r=new Rig();var before=r.Revision;var audits=r.F.Count("AuditEvents");var good=r.Preset();
        var bootstrap=Guid.NewGuid();
        RolePreset[] bad=[new("unauthorized",good.Hosts,[new(Guid.NewGuid(),r.Grantee,ServerCapability.DeleteServer,r.Target,Use,r.ServerRoot)]),
            new("cross-host",[new(Guid.NewGuid(),r.Grantee,HostCapability.CreateServer,r.Other,Use,r.HostRoot)],good.Servers),
            new("cross-server",good.Hosts,[new(Guid.NewGuid(),r.Grantee,ServerCapability.ViewServer,new(r.Other,r.Target.ServerProfileId),Use,r.ServerRoot)]),
            new("bootstrap",[new(bootstrap,r.Peer,HostCapability.CreateServer,r.F.HostId,Onward,r.HostRoot),new(Guid.NewGuid(),r.Grantee,HostCapability.CreateServer,r.F.HostId,Use,bootstrap)],[])];
        foreach(var recipe in bad)Reject<UnauthorizedAccessException>(()=>r.Repo.ApplyRemotePreset(r.Actor,before,recipe));
        Check(r.Revision==before&&r.F.Count("AuditEvents")==audits);
        var result=r.Repo.ApplyRemotePreset(r.Actor,before,good);Check(result.Revision==before+2&&result.HostGrantIds.Count==1&&result.ServerGrantIds.Count==1);
        Reject<NotSupportedException>(()=>((IList<Guid>)result.HostGrantIds).Clear());
        using var cmd=r.F.Writer.CreateCommand();cmd.CommandText="SELECT Summary FROM AuditEvents WHERE ActorKind='RemoteManager';";using var reader=cmd.ExecuteReader();int count=0;
        while(reader.Read()){var text=reader.GetString(0);Check(text.Contains("RolePreset:"+result.AuditBatchId!.Value.ToString("D"))&&!text.Contains("private-fixture-label"));count++;}
        Check(count==2);return Task.CompletedTask;
    }
    public static Task ConcurrentRemoteWritersAndExactRevocation()
    {
        using var r=new Rig();var actor=r.Actor;var before=r.Revision;int won=0,stale=0;
        Parallel.For(0,8,_=>{try{r.Repo.ApplyRemotePreset(actor,before,r.Preset());Interlocked.Increment(ref won);}catch(StaleAuthorizationRevisionException){Interlocked.Increment(ref stale);}});
        Check(won==1&&stale==7&&r.Revision==before+2);
        r.Repo.InvalidateHostSubtree(r.Owner,r.Revision,r.HostRoot);var snapshot=r.Repo.Read();
        Check(!snapshot.Policy.CanUseHost(r.Grantee,HostCapability.CreateServer,r.F.HostId)&&snapshot.Policy.CanUseServer(r.Grantee,ServerCapability.ViewServer,r.Target));
        Reject<UnauthorizedAccessException>(()=>r.Host());return Task.CompletedTask;
    }
    private sealed class CancelAtGrant(TimeProvider actual,CancellationTokenSource cancellation):TimeProvider
    {public override DateTimeOffset GetUtcNow(){cancellation.Cancel();return actual.GetUtcNow();}}
    public static Task EmptyCancellationAndTrustedPendingEvidence()
    {
        using var r=new Rig();var before=r.Revision;var audits=r.F.Count("AuditEvents");var empty=new RolePreset("empty",[],[]);
        var result=r.Repo.ApplyRemotePreset(r.Actor,before,empty);Check(result.AuditBatchId is null&&result.Revision==before);
        Reject<StaleAuthorizationRevisionException>(()=>r.Repo.ApplyRemotePreset(r.Actor,before-1,empty));
        foreach(var preset in new[]{false,true})
        {
            using var ct=new CancellationTokenSource();var repo=new GrantPolicyRepository(r.F.Database,r.F.HostId,new CancelAtGrant(r.F.Time,ct));
            if(preset)Reject<OperationCanceledException>(()=>repo.ApplyRemotePreset(r.Actor,before,r.Preset(),ct.Token));
            else Reject<OperationCanceledException>(()=>repo.IssueRemoteHost(r.Actor,before,Guid.NewGuid(),r.Grantee,HostCapability.CreateServer,r.F.HostId,Use,r.HostRoot,ct.Token));
            Check(r.Revision==before&&r.F.Count("AuditEvents")==audits);
        }
        r.F.Execute($"UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint='{new string('C',64)}',PendingRotationId='{Guid.NewGuid():D}',PendingRotationExpiresUtc='{r.F.Time.Now.AddMinutes(-1):O}',PendingReconfirmationRequired=1 WHERE PeerHostId='{r.F.PeerId:D}';");
        Check(r.Repo.ApplyRemotePreset(r.Actor with{PeerFingerprint=new('C',64)},r.Revision,r.Preset()).HostGrantIds.Count==1);
        return Task.CompletedTask;
    }
}
