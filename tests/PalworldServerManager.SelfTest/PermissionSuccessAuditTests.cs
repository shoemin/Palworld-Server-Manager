using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static class PermissionSuccessAuditTests
{
    private static readonly DelegationRights Use=new(false,false),Onward=new(true,true);
    private static readonly string LocalPin=new('A',64),PeerPin=new('B',64);
    private static void Check(bool value){if(!value)throw new Exception("Successful permission audit assertion failed.");}
    private static void Reject(Action action)
    {try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected exact successful audit refusal.");}
    private sealed class Rig:IDisposable
    {
        internal readonly PeerTrustTests.Fixture F=new();
        internal readonly Guid User=Guid.NewGuid();
        internal readonly ServerRef Target;
        internal GrantPolicyRepository Repo=>new(F.Database,F.HostId,F.Time);
        internal LocalPrincipalMutationActor Owner=>new(F.HostId,F.OwnerId,"native-owner","fixture-public");
        internal LocalPrincipalMutationActor Local=>new(F.HostId,User,"fixture-user","fixture-user-key");
        internal ActorRef PeerActor=>ActorRef.RemoteManager(F.PeerId);
        internal ActorRef UserActor=>ActorRef.LocalPrincipal(User);
        internal long Revision=>Repo.Read().Revision;
        internal PeerGrantMutationActor Peer=>new(F.HostId,F.PeerId,PeerPin,LocalPin,HostDatabase.QueryScalarLong(F.Writer,$"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{F.PeerId:D}';"));
        internal Rig(bool active=true)
        {
            Target=new(F.HostId,Guid.NewGuid());
            F.Execute($"INSERT INTO LocalPrincipals (LocalPrincipalId,OsPrincipalRef,PublicVerificationKey,IsOwner,State,CreatedUtc) VALUES ('{User:D}','fixture-user','fixture-user-key',0,'Active','{F.Time.Now:O}');");
            if(active)F.Execute($"INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('{F.PeerId:D}','Active','{PeerPin}','{F.Time.Now:O}');");
        }
        internal Guid Root(bool server,ActorRef actor)
        {
            var id=Guid.NewGuid();
            if(server)Repo.IssueServer(Owner,Revision,id,actor,ServerCapability.ViewServer,Target,Onward,null);
            else Repo.IssueHost(Owner,Revision,id,actor,HostCapability.CreateServer,F.HostId,Onward,null);
            return id;
        }
        internal void Register(SqliteConnection c,SqliteTransaction tx)
        {
            using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText=$"INSERT INTO ServerInventory (ServerProfileId,AuthoritativeHostId,DisplayName,InstallPath,CreatedUtc) VALUES ('{Target.ServerProfileId:D}','{F.HostId:D}','fixture','fixture','{F.Time.Now:O}');";cmd.ExecuteNonQuery();
        }
        internal string Snapshot()
        {
            var result=new List<object>();
            foreach(var table in new[]{"HostCapabilityGrants","ServerCapabilityGrants","AuditEvents","DefaultGrantTemplateState","HostDefaultGrants","ServerDefaultGrants","TrustedManagers","PeerRelationshipIncarnations","ServerInventory","AuthorizationRevision"})
            {
                using var cmd=F.Writer.CreateCommand();cmd.CommandText="SELECT * FROM "+table+" ORDER BY 1;";using var reader=cmd.ExecuteReader();
                var rows=new List<object>();while(reader.Read()){var row=new object[reader.FieldCount];reader.GetValues(row);rows.Add(row);}result.Add(rows);
            }
            return JsonSerializer.Serialize(result);
        }
        public void Dispose()=>F.Dispose();
    }
    private sealed record Attempt(Action Run,string Kind,int Audits);
    private static Attempt Prepare(Rig r,int path)
    {
        if(path is 0 or 1)
        {
            var revision=r.Revision;var id=Guid.NewGuid();
            return new(()=>{if(path==0)r.Repo.IssueHost(r.Owner,revision,id,r.UserActor,HostCapability.CreateServer,r.F.HostId,Use,null);else r.Repo.IssueServer(r.Owner,revision,id,r.UserActor,ServerCapability.ViewServer,r.Target,Use,null);},"CapabilityGrantIssued",1);
        }
        if(path is 2 or 3)
        {
            var root=r.Root(path==3,r.PeerActor);var proof=r.Peer;var revision=r.Revision;var id=Guid.NewGuid();
            return new(()=>{if(path==2)r.Repo.IssueRemoteHost(proof,revision,id,r.UserActor,HostCapability.CreateServer,r.F.HostId,Use,root);else r.Repo.IssueRemoteServer(proof,revision,id,r.UserActor,ServerCapability.ViewServer,r.Target,Use,root);},"CapabilityGrantIssued",1);
        }
        if(path is 4 or 5)
        {
            Guid? host=null,server=null;if(path==5){host=r.Root(false,r.PeerActor);server=r.Root(true,r.PeerActor);}
            var preset=new RolePreset("private-label",[new(Guid.NewGuid(),r.UserActor,HostCapability.CreateServer,r.F.HostId,Use,host)],[new(Guid.NewGuid(),r.UserActor,ServerCapability.ViewServer,r.Target,Use,server)]);
            var proof=r.Peer;var revision=r.Revision;
            return new(()=>{if(path==4)r.Repo.ApplyPreset(r.Owner,revision,preset);else r.Repo.ApplyRemotePreset(proof,revision,preset);},"CapabilityGrantIssued",2);
        }
        if(path==6)
        {
            var revision=r.Revision;
            return new(()=>r.Repo.ConfigureDefaults(r.Owner,revision,new([new(HostCapability.CreateServer,Use)],[new(ServerCapability.ViewServer,r.Target,Use)])),"DefaultGrantTemplateConfigured",1);
        }
        if(path==7)
        {
            var host=r.Root(false,r.PeerActor);var server=r.Root(true,r.PeerActor);
            r.Repo.InvalidateHostSubtree(r.Owner,r.Revision,host);r.Repo.InvalidateServerSubtree(r.Owner,r.Revision,server);var revision=r.Revision;
            return new(()=>r.Repo.ReissueHistoricalPeerRoots(r.Owner,revision,r.F.PeerId,[host],[server]),"CapabilityGrantReissued",2);
        }
        if(path==8)
        {
            r.Root(false,r.PeerActor);var proof=r.Peer;var revision=r.Revision;
            return new(()=>r.Repo.CommitConfirmedRemoteCreation(proof,revision,r.Target,r.Register),"CreatorGrantApplied",6);
        }
        if(path is 9 or 10)
        {
            var root=r.Root(path==10,r.UserActor);
            if(path==9)r.Repo.IssueHost(r.Local,r.Revision,Guid.NewGuid(),r.PeerActor,HostCapability.CreateServer,r.F.HostId,Use,root);
            else r.Repo.IssueServer(r.Local,r.Revision,Guid.NewGuid(),r.PeerActor,ServerCapability.ViewServer,r.Target,Use,root);
            var revision=r.Revision;
            return new(()=>{if(path==9)r.Repo.InvalidateHostSubtree(r.Owner,revision,root);else r.Repo.InvalidateServerSubtree(r.Owner,revision,root);},"CapabilityGrantSubtreeInvalidated",1);
        }
        r.Repo.ConfigureDefaults(r.Owner,r.Revision,new([new(HostCapability.CreateServer,Use)],[new(ServerCapability.ViewServer,r.Target,Use)]));
        r.F.Repository.RecordVerifiedBinding(r.F.PeerId,PeerPin,LocalPin);
        return new(()=>
        {
            if(path==11)r.F.Repository.AcceptActivationAcknowledgement(r.F.PeerId,PeerPin,LocalPin,new(r.F.PeerId,r.F.HostId,LocalPin),r.Repo.CreateDefaultActivationHook());
            else r.F.Repository.AcceptOwnerActivationAcknowledgement(r.Owner,r.F.PeerId,PeerPin,LocalPin,new(r.F.PeerId,r.F.HostId,LocalPin),r.Repo.CreateDefaultActivationHook());
        },"DefaultGrantApplied",3);
    }
    public static Task RemovedAuditRollsBackGrant()
    {
        using var f=new PeerTrustTests.Fixture();
        var repo=new GrantPolicyRepository(f.Database,f.HostId,f.Time);
        var owner=new LocalPrincipalMutationActor(f.HostId,f.OwnerId,"native-owner","fixture-public");
        var before=repo.Read().Revision;
        f.Execute("CREATE TRIGGER SuccessFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='CapabilityGrantIssued' BEGIN DELETE FROM AuditEvents WHERE AuditEventId=NEW.AuditEventId; END;");
        bool rejected=false;
        try{repo.IssueHost(owner,before,Guid.NewGuid(),ActorRef.LocalPrincipal(f.OwnerId),HostCapability.CreateServer,f.HostId,new(false,false),null);}
        catch(InvalidOperationException){rejected=true;}
        if(!rejected||repo.Read().Revision!=before||f.Count("HostCapabilityGrants")!=0)
            throw new Exception("Successful permission mutation committed without its required audit.");
        return Task.CompletedTask;
    }
    public static Task EverySuccessfulPathChecksEveryAuditField()
    {
        for(var path=0;path<13;path++)for(var mode=0;mode<11;mode++)
        {
            using var r=new Rig(path<11);var attempt=Prepare(r,path);var before=r.Snapshot();var audits=r.F.Count("AuditEvents");
            var changed=Guid.NewGuid().ToString("D");
            var assignment=mode switch
            {
                1=>"AuditEventId='00000000-0000-0000-0000-' || lower(hex(randomblob(6)))",2=>"OccurredUtc='2000-01-01T00:00:00.0000000+00:00'",3=>"EventKind='Changed'",
                4=>"ActorKind=NULL",5=>$"ActorLocalPrincipalId=CASE WHEN ActorLocalPrincipalId IS NULL THEN '{changed}' ELSE NULL END",
                6=>$"ActorPeerHostId=CASE WHEN ActorPeerHostId IS NULL THEN '{changed}' ELSE NULL END",7=>$"AffectedHostId='{changed}'",
                8=>$"AffectedServerProfileId=CASE WHEN AffectedServerProfileId IS NULL THEN '{changed}' ELSE NULL END",
                9=>"IsOfflineRecovery=1",_=>"Summary='changed'"
            };
            var sql=mode==0?"DELETE FROM AuditEvents WHERE AuditEventId=NEW.AuditEventId;":$"UPDATE AuditEvents SET {assignment} WHERE AuditEventId=NEW.AuditEventId;";
            r.F.Execute($"CREATE TRIGGER SuccessFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='{attempt.Kind}' BEGIN {sql} END;");
            Reject(attempt.Run);Check(r.Snapshot()==before);
            r.F.Execute("DROP TRIGGER SuccessFault;");attempt.Run();Check(r.F.Count("AuditEvents")==audits+attempt.Audits);
            Check(HostDatabase.QueryScalarLong(r.F.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PermissionPolicyDenied';")==0);
        }
        return Task.CompletedTask;
    }
    public static Task LaterBatchAndEnclosingAuditCannotEraseEarlierRows()
    {
        foreach(var path in new[]{4,5,7,8,11,12})foreach(var erase in new[]{false,true})
        {
            using var r=new Rig(path<11);var attempt=Prepare(r,path);var before=r.Snapshot();var audits=r.F.Count("AuditEvents");
            var triggerKind=path>=11?"PeerActivated":attempt.Kind;
            var watermark=HostDatabase.QueryScalarLong(r.F.Writer,"SELECT COALESCE(MAX(rowid),0) FROM AuditEvents;");
            var selector=$"rowid=(SELECT MIN(rowid) FROM AuditEvents WHERE EventKind='{attempt.Kind}' AND rowid>{watermark} AND AuditEventId<>NEW.AuditEventId)";
            var sql=erase?$"DELETE FROM AuditEvents WHERE {selector};":$"UPDATE AuditEvents SET Summary='later-audit-change' WHERE {selector};";
            r.F.Execute($"CREATE TRIGGER SuccessFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='{triggerKind}' BEGIN {sql} END;");
            Reject(attempt.Run);Check(r.Snapshot()==before);
            r.F.Execute("DROP TRIGGER SuccessFault;");attempt.Run();Check(r.F.Count("AuditEvents")==audits+attempt.Audits);
        }
        return Task.CompletedTask;
    }
    public static Task ConcurrentSuccessfulBatchesKeepIndependentAuditGuards()
    {
        using var r=new Rig();var revision=r.Revision;var audits=r.F.Count("AuditEvents");var owner=r.Owner;var grantee=r.UserActor;int won=0,stale=0;
        Parallel.For(0,8,_=>
        {
            var preset=new RolePreset("private-label",[new(Guid.NewGuid(),grantee,HostCapability.CreateServer,r.F.HostId,Use)],[new(Guid.NewGuid(),grantee,ServerCapability.ViewServer,r.Target,Use)]);
            try{new GrantPolicyRepository(r.F.Database,r.F.HostId,r.F.Time).ApplyPreset(owner,revision,preset);Interlocked.Increment(ref won);}
            catch(StaleAuthorizationRevisionException){Interlocked.Increment(ref stale);}
        });
        Check(won==1&&stale==7&&r.Revision==revision+2&&r.F.Count("AuditEvents")==audits+2&&r.F.Count("HostCapabilityGrants")==1&&r.F.Count("ServerCapabilityGrants")==1);
        return Task.CompletedTask;
    }
}
