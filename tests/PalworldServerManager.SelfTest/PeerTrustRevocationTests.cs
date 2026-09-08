using System.Security.Authentication;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static partial class PeerTrustRevocationTests
{
    private static readonly DelegationRights Use=new(false,false),Onward=new(true,true);
    private static void Check(bool value){if(!value)throw new Exception("Trust revocation assertion failed.");}
    private static void Reject<T>(Action action)where T:Exception
    {try{action();}catch(T){return;}throw new Exception("Expected revocation refusal: "+typeof(T).Name);}
    private sealed class Rig:IDisposable
    {
        internal readonly PeerTrustTests.Fixture F=new();
        internal readonly Guid Other=Guid.NewGuid(),User=Guid.NewGuid();
        internal GrantPolicyRepository Repo=>new(F.Database,F.HostId,F.Time);
        internal LocalPrincipalMutationActor Owner=>new(F.HostId,F.OwnerId,"native-owner","fixture-public");
        internal LocalPrincipalMutationActor Local=>new(F.HostId,User,"native-user","user-public");
        internal long Revision=>Repo.Read().Revision;
        internal long Incarnation=>HostDatabase.QueryScalarLong(F.Writer,$"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{F.PeerId:D}';");
        internal PeerGrantMutationActor Peer=>new(F.HostId,F.PeerId,new('B',64),new('A',64),Incarnation);
        internal readonly ServerRef Target;
        internal Rig(bool forest=true)
        {
            Target=new(F.HostId,Guid.NewGuid());
            F.Repository.RecordVerifiedBinding(F.PeerId,new('B',64),new('A',64));
            F.Repository.RecordVerifiedBinding(Other,new('C',64),new('A',64));
            // Representative current trust/identity fixture; not a claim of an RPC ceremony.
            F.Execute($"UPDATE TrustedManagers SET State='Active'; INSERT INTO LocalPrincipals (LocalPrincipalId,OsPrincipalRef,PublicVerificationKey,IsOwner,State,CreatedUtc) VALUES ('{User:D}','native-user','user-public',0,'Active','{F.Time.Now:O}');");
            if(!forest)return;
            var p=ActorRef.RemoteManager(F.PeerId);var other=ActorRef.RemoteManager(Other);
            var h=Guid.NewGuid();var s=Guid.NewGuid();
            Repo.IssueHost(Owner,Revision,h,p,HostCapability.ManageHostSettings,F.HostId,Onward,null);
            Repo.IssueServer(Owner,Revision,s,p,ServerCapability.ViewServer,Target,Onward,null);
            Repo.IssueRemoteHost(Peer,Revision,Guid.NewGuid(),other,HostCapability.ManageHostSettings,F.HostId,Use,h);
            Repo.IssueRemoteServer(Peer,Revision,Guid.NewGuid(),other,ServerCapability.ViewServer,Target,Use,s);
            Repo.IssueHost(Owner,Revision,Guid.NewGuid(),other,HostCapability.ManageHostSettings,F.HostId,Use,null);
            Repo.IssueServer(Owner,Revision,Guid.NewGuid(),other,ServerCapability.ViewServer,Target,Use,null);
            Repo.IssueHost(Owner,Revision,Guid.NewGuid(),p,HostCapability.ManageHostSettings,Other,Use,null);
            F.Repository.RecordVerifiedBinding(F.PeerId,new('D',64),new('A',64));
            F.Repository.RecordVerifiedBinding(Other,new('E',64),new('A',64));
        }
        internal PeerTrustRevocationResult Revoke()=>Repo.RevokeLocalPeerTrust(Owner,Revision,F.PeerId,Incarnation);
        internal string Snapshot()
        {
            var tables=new[]{"HostIdentity","SecureCredentialReferences","HostCredentialRotations","LocalPrincipals","TrustedManagers","PeerRelationshipIncarnations","TrustedManagerPairings","PeerLocalBindingEvidence","PendingCredentialReplacements","PeerReplacementBindingEvidence","PeerReplacementCompletions","PeerRecoveryCompletionReceipts","PeerUnpairReceipts","HostCapabilityGrants","ServerCapabilityGrants","DefaultGrantTemplateState","HostDefaultGrants","ServerDefaultGrants","AuthorizationRevision","AuditEvents"};
            var rows=new List<string>();
            foreach(var table in tables)
            {
                using var cmd=F.Writer.CreateCommand();cmd.CommandText=$"SELECT * FROM {table} ORDER BY 1;";using var reader=cmd.ExecuteReader();
                while(reader.Read()){var values=new object[reader.FieldCount];reader.GetValues(values);rows.Add(table+JsonSerializer.Serialize(values));}
            }
            return string.Join('\n',rows);
        }
        public void Dispose()=>F.Dispose();
    }
    public static Task AtomicForestsTombstoneAndAudit()
    {
        using var r=new Rig();var before=r.Repo.Read();var incarnation=r.Incarnation;var other=r.F.Repository.Read(r.Other);
        Check(r.F.Repository.RecognizesTransportFingerprint(new('B',64)));
        var result=r.Revoke();var after=r.Repo.Read();var trust=r.F.Repository.Read(r.F.PeerId)!;
        Check(result.Changed&&result.InvalidatedGrants==5&&result.InvalidatedReplacements==1&&result.Revision==before.Revision+6&&result.Incarnation>incarnation);
        Check(trust is {State:"Revoked",CurrentFingerprint:null,PendingFingerprint:null,PendingRotationId:null,PendingRotationExpiresUtc:null,RecoveryRequired:false,PendingReconfirmationRequired:false});
        Check(!r.F.Repository.RecognizesTransportFingerprint(new('B',64))&&r.F.Repository.Read(r.Other)==other);
        Check(after.HostGrants.Count(g=>g.InvalidatedUtc is not null)==3&&after.ServerGrants.Count(g=>g.InvalidatedUtc is not null)==2);
        foreach(var grant in before.HostGrants.Where(g=>g.GranteeActor.Id==r.Other&&g.DerivedFromGrantId is null))Check(after.HostGrants.Contains(grant));
        foreach(var grant in before.ServerGrants.Where(g=>g.GranteeActor.Id==r.Other&&g.DerivedFromGrantId is null))Check(after.ServerGrants.Contains(grant));
        Check(after.Policy.CanUseServer(ActorRef.RemoteManager(r.Other),ServerCapability.ViewServer,r.Target)&&after.Policy.IsOwner(ActorRef.LocalPrincipal(r.F.OwnerId)));
        using var cmd=r.F.Writer.CreateCommand();cmd.CommandText="SELECT ActorKind,ActorLocalPrincipalId,ActorPeerHostId,AffectedHostId,Summary FROM AuditEvents WHERE EventKind='PeerTrustRevoked';";
        using var reader=cmd.ExecuteReader();Check(reader.Read()&&reader.GetString(0)=="LocalPrincipal"&&reader.GetString(1)==r.F.OwnerId.ToString("D")&&reader.IsDBNull(2)&&reader.GetString(3)==r.F.HostId.ToString("D"));
        var summary=reader.GetString(4);Check(summary.Contains(r.F.PeerId.ToString("D"))&&!summary.Contains("fixture-public")&&!summary.Contains(new string('B',64))&&!reader.Read());
        return Task.CompletedTask;
    }
    public static Task LocalCapabilityAndIdentityBoundaries()
    {
        foreach(var cap in new[]{HostCapability.ManagePermissions,HostCapability.ManageTrustedManagers})
        {
            using var r=new Rig(false);r.Repo.IssueHost(r.Owner,r.Revision,Guid.NewGuid(),ActorRef.LocalPrincipal(r.User),cap,cap==HostCapability.ManagePermissions?r.F.HostId:r.Other,Use,null);
            var before=r.Revision;Reject<UnauthorizedAccessException>(()=>r.Repo.RevokeLocalPeerTrust(r.Local,before,r.F.PeerId,r.Incarnation));
            Check(r.Revision==before&&r.F.Repository.Read(r.F.PeerId)!.State=="Active");
            var snapshot=r.Snapshot();Reject<AuthenticationException>(()=>r.Repo.RevokeLocalPeerTrust(r.Owner with{PublicVerificationKey="stale"},before,r.F.PeerId,r.Incarnation));Check(r.Snapshot()==snapshot);
            Reject<ArgumentException>(()=>r.Repo.RevokeLocalPeerTrust(r.Owner,before,r.F.HostId,r.Incarnation));Check(r.Snapshot()==snapshot);
        }
        using(var r=new Rig(false))
        {
            var root=Guid.NewGuid();r.Repo.IssueHost(r.Owner,r.Revision,root,ActorRef.RemoteManager(r.F.PeerId),HostCapability.ManageTrustedManagers,r.F.HostId,Onward,null);
            r.Repo.IssueRemoteHost(r.Peer,r.Revision,Guid.NewGuid(),ActorRef.LocalPrincipal(r.User),HostCapability.ManageTrustedManagers,r.F.HostId,Use,root);
            var result=r.Repo.RevokeLocalPeerTrust(r.Local,r.Revision,r.F.PeerId,r.Incarnation);
            Check(result.Changed&&result.InvalidatedGrants==2&&!r.Repo.Read().Policy.CanUseHost(ActorRef.LocalPrincipal(r.User),HostCapability.ManageTrustedManagers,r.F.HostId));
            Check(r.Repo.Read().Policy.IsOwner(ActorRef.LocalPrincipal(r.F.OwnerId)));
        }
        return Task.CompletedTask;
    }
    public static Task IdempotenceAndFreshPairingGate()
    {
        using var r=new Rig();var first=r.Revoke();var snapshot=r.Snapshot();var second=r.Revoke();
        Check(!second.Changed&&second.Revision==first.Revision&&second.Incarnation==first.Incarnation&&r.Snapshot()==snapshot);
        var candidate=r.F.Repository.RecordVerifiedBinding(r.F.PeerId,new('B',64),new('A',64));
        Check(candidate.Disposition==PeerBindingDisposition.ReplacementRequired&&r.F.Repository.Read(r.F.PeerId)!.State=="Revoked");
        Check(!r.Repo.Read().Policy.IsActive(ActorRef.RemoteManager(r.F.PeerId))&&r.Repo.Read().HostGrants.Count(g=>g.InvalidatedUtc is not null)==3);
        var again=r.Revoke();Check(again.Changed&&again.InvalidatedGrants==0&&again.InvalidatedReplacements==1&&again.Incarnation==first.Incarnation);
        snapshot=r.Snapshot();Check(!r.Revoke().Changed&&r.Snapshot()==snapshot);return Task.CompletedTask;
    }
    private sealed class CancelClock(CancellationTokenSource cancellation,DateTimeOffset now):TimeProvider
    {public override DateTimeOffset GetUtcNow(){cancellation.Cancel();return now;}}
    public static Task StaleRequestAndCancellationRollback()
    {
        using var r=new Rig();var incarnation=r.Incarnation;var revision=r.Revision;
        r.F.Execute($"UPDATE TrustedManagerPairings SET BoundUtc=BoundUtc WHERE PeerHostId='{r.F.PeerId:D}';");
        Check(r.Revision==revision&&r.Incarnation>incarnation);var snapshot=r.Snapshot();
        Reject<InvalidOperationException>(()=>r.Repo.RevokeLocalPeerTrust(r.Owner,revision,r.F.PeerId,incarnation));Check(r.Snapshot()==snapshot);
        Reject<StaleAuthorizationRevisionException>(()=>r.Repo.RevokeLocalPeerTrust(r.Owner,revision-1,r.F.PeerId,r.Incarnation));Check(r.Snapshot()==snapshot);
        using var cancellation=new CancellationTokenSource();var repo=new GrantPolicyRepository(r.F.Database,r.F.HostId,new CancelClock(cancellation,r.F.Time.Now));
        Reject<OperationCanceledException>(()=>repo.RevokeLocalPeerTrust(r.Owner,revision,r.F.PeerId,r.Incarnation,cancellation.Token));Check(r.Snapshot()==snapshot);
        return Task.CompletedTask;
    }
    public static Task EveryLateEffectAndAuditFailureRollsBack()
    {
        var faults=new List<string>{
            "DELETE FROM AuditEvents WHERE AuditEventId=NEW.AuditEventId;",
            "UPDATE AuditEvents SET Summary='changed' WHERE AuditEventId=NEW.AuditEventId;",
            "UPDATE AuditEvents SET EventKind='changed' WHERE AuditEventId=NEW.AuditEventId;",
            "UPDATE AuditEvents SET IsOfflineRecovery=1 WHERE AuditEventId=NEW.AuditEventId;",
            "UPDATE TrustedManagers SET DisplayName='changed' WHERE PeerHostId='$peer';",
            "UPDATE TrustedManagers SET RevokedUtc='changed' WHERE PeerHostId='$peer';",
            "UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='$pin' WHERE PeerHostId='$peer';",
            "UPDATE PendingCredentialReplacements SET InvalidatedUtc=NULL WHERE PeerHostId='$peer';",
            "UPDATE PendingCredentialReplacements SET ProposedKeyFingerprint='$pin' WHERE PeerHostId='$peer';",
            "DELETE FROM PendingCredentialReplacements WHERE PeerHostId='$peer';",
            "UPDATE HostCapabilityGrants SET InvalidatedUtc=NULL WHERE GranteePeerHostId='$peer';",
            "UPDATE ServerCapabilityGrants SET InvalidatedUtc=NULL WHERE GranteePeerHostId='$peer';",
            "UPDATE HostCapabilityGrants SET InvalidatedUtc='2026-09-06T12:00:00.0000000+00:00' WHERE GranteePeerHostId='$other';",
            "UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;",
            "UPDATE SecureCredentialReferences SET RetiredUtc='changed' WHERE CredentialRef='current';",
            "DELETE FROM PeerRelationshipIncarnations WHERE PeerHostId='$peer'; INSERT INTO PeerRelationshipIncarnations (PeerHostId) VALUES ('$peer');",
            "UPDATE PendingCredentialReplacements SET ProposedKeyFingerprint='$pin' WHERE PeerHostId='$other';"
        };
        foreach(var column in new[]{"AuditEventId","OccurredUtc","ActorKind","ActorLocalPrincipalId","ActorPeerHostId","AffectedHostId","AffectedServerProfileId"})
            faults.Add($"UPDATE AuditEvents SET {column}='changed' WHERE AuditEventId=NEW.AuditEventId;");
        foreach(var fault in faults)
        {
            using var r=new Rig();var sql=fault.Replace("$peer",r.F.PeerId.ToString("D")).Replace("$other",r.Other.ToString("D")).Replace("$pin",new string('F',64));
            r.F.Execute($"CREATE TRIGGER RevocationFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PeerTrustRevoked' BEGIN {sql} END;");
            var snapshot=r.Snapshot();bool refused=false;
            try{r.Revoke();}catch(Exception e)when(e is InvalidOperationException or InvalidDataException or SqliteException or AuthenticationException or StaleAuthorizationRevisionException){refused=true;}
            if(!refused||r.Snapshot()!=snapshot)throw new Exception("Revocation fault did not roll back: "+fault);
        }
        return Task.CompletedTask;
    }
    public static async Task ConcurrentRevocationsHaveOneWinner()
    {
        using var r=new Rig();var revision=r.Revision;var incarnation=r.Incarnation;var won=0;var stale=0;
        await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>
        {try{if(r.Repo.RevokeLocalPeerTrust(r.Owner,revision,r.F.PeerId,incarnation).Changed)Interlocked.Increment(ref won);}
            catch(StaleAuthorizationRevisionException){Interlocked.Increment(ref stale);}})));
        Check(won==1&&stale==7&&r.F.Repository.Read(r.F.PeerId)!.State=="Revoked");
    }
    public static Task HistoricalInvalidationRemainsPermanent()
    {
        using var r=new Rig();var stamp=r.F.Time.Now.AddDays(-1).ToString("O");
        r.F.Execute($"UPDATE HostCapabilityGrants SET InvalidatedUtc='{stamp}' WHERE GranteePeerHostId='{r.F.PeerId:D}' AND DerivedFromGrantId IS NULL;");
        var old=r.Repo.Read().HostGrants.Where(g=>g.InvalidatedUtc is not null).ToArray();Check(old.Length==2);
        var result=r.Revoke();Check(result.InvalidatedGrants==3&&old.All(g=>r.Repo.Read().HostGrants.Contains(g)));
        // Even an artificial later Active restoration cannot resurrect the old forest.
        r.F.Execute($"UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='{new string('B',64)}' WHERE PeerHostId='{r.F.PeerId:D}';");
        Check(!r.Repo.Read().Policy.CanUseHost(ActorRef.RemoteManager(r.F.PeerId),HostCapability.ManageHostSettings,r.F.HostId));
        Check(!r.Repo.Read().Policy.CanUseServer(ActorRef.RemoteManager(r.F.PeerId),ServerCapability.ViewServer,r.Target));
        return Task.CompletedTask;
    }
    public static Task PeerBoundAndRecoveryStatesAreRevocable()
    {
        foreach(var recovery in new[]{false,true})
        {
            using var r=new Rig(false);
            r.F.Execute($"UPDATE TrustedManagers SET State='{(recovery?"Active":"PeerBound")}',PeerRecoveryRequired={(recovery?1:0)},PendingTrustedPublicKeyFingerprint='{new string('D',64)}',PendingRotationId='{Guid.NewGuid():D}',PendingRotationExpiresUtc='{r.F.Time.Now.AddMinutes(-1):O}',PendingReconfirmationRequired=1 WHERE PeerHostId='{r.F.PeerId:D}';");
            Check(r.Revoke().Changed&&r.F.Repository.Read(r.F.PeerId) is{State:"Revoked",RecoveryRequired:false,PendingFingerprint:null,PendingReconfirmationRequired:false});
        }
        return Task.CompletedTask;
    }
}
