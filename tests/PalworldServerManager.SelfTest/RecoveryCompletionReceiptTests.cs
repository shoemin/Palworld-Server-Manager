using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;

namespace PalworldServerManager.SelfTest;

internal static partial class PeerTrustRevocationTests
{
    private static readonly string RecoveryLocalPin=new('A',64);
    private static void RecoveryPending(Rig r)=>r.F.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{r.F.PeerId:D}';");
    private static PeerRecoveryCompletionResult ReceiveRecovery(Rig r,PeerGrantMutationActor proof,Guid approval,string? fingerprint=null)
        =>r.Repo.ReceiveAuthenticatedRecoveryCompletion(proof,approval,fingerprint??RecoveryLocalPin);
    public static Task RecoveryReceiptClearsExactKeyWithoutNewGrants()
    {
        using var r=new Rig();RecoveryPending(r);var proof=r.Peer;var before=r.Repo.Read();var other=r.F.Repository.Read(r.Other);
        var request=ReplacementRequest(r);var approval=Guid.NewGuid();var result=ReceiveRecovery(r,proof,approval);var after=r.Repo.Read();
        Check(result.RecoveryCleared&&result.Disposition==RecoveryCompletionDisposition.Recorded&&result.Incarnation>proof.Incarnation&&result.Revision==before.Revision+1);
        Check(!r.F.Repository.Read(r.F.PeerId)!.RecoveryRequired&&r.F.Repository.Read(r.Other)==other);
        Check(before.HostGrants.SequenceEqual(after.HostGrants)&&before.ServerGrants.SequenceEqual(after.ServerGrants));
        Check(after.Policy.CanUseHost(ActorRef.RemoteManager(r.F.PeerId),HostCapability.ManageHostSettings,r.F.HostId));
        Check(HostDatabase.QueryScalarLong(r.F.Writer,$"SELECT COUNT(*) FROM PendingCredentialReplacements WHERE ReplacementId='{request:D}' AND InvalidatedUtc='{r.F.Time.Now:O}';")==1);
        Check(HostDatabase.QueryScalarLong(r.F.Writer,$"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerRecoveryCompletionReceived' AND ActorKind='RemoteManager' AND ActorPeerHostId='{r.F.PeerId:D}' AND ActorLocalPrincipalId IS NULL AND IsOfflineRecovery=0;")==1);
        Check(r.F.Count("PeerRecoveryCompletionReceipts")==1);
        return Task.CompletedTask;
    }
    public static Task RecoveryReceiptDuplicateAndNoOpAreBounded()
    {
        using var r=new Rig(false);RecoveryPending(r);var proof=r.Peer;var approval=Guid.NewGuid();var first=ReceiveRecovery(r,proof,approval);var snapshot=r.Snapshot();
        Check(ReceiveRecovery(r,proof,approval).Disposition==RecoveryCompletionDisposition.AlreadyRecorded&&snapshot==r.Snapshot());
        Check(ReceiveRecovery(r,r.Peer,approval).Disposition==RecoveryCompletionDisposition.AlreadyRecorded&&snapshot==r.Snapshot());
        var later=ReceiveRecovery(r,r.Peer,Guid.NewGuid());
        Check(later.Disposition==RecoveryCompletionDisposition.Recorded&&!later.RecoveryCleared&&later.Incarnation==first.Incarnation&&later.Revision==first.Revision);
        Check(r.F.Count("PeerRecoveryCompletionReceipts")==1);
        Reject<AuthenticationException>(()=>ReceiveRecovery(r,proof,approval));
        using var clean=new Rig(false);var revision=clean.Revision;var noOp=ReceiveRecovery(clean,clean.Peer,Guid.NewGuid());
        Check(!noOp.RecoveryCleared&&noOp.Revision==revision&&noOp.Disposition==RecoveryCompletionDisposition.Recorded&&clean.F.Count("HostCapabilityGrants")==0);
        return Task.CompletedTask;
    }
    public static Task RecoveryReceiptMismatchAndSecondRecoveryCannotUnlock()
    {
        using var r=new Rig(false);RecoveryPending(r);var proof=r.Peer;var approval=Guid.NewGuid();var before=r.Snapshot();var auditCount=r.F.Count("AuditEvents");
        var mismatch=ReceiveRecovery(r,proof,approval,new('F',64));
        Check(mismatch.Disposition==RecoveryCompletionDisposition.KeyMismatch&&!mismatch.RecoveryCleared&&r.F.Repository.Read(r.F.PeerId)!.RecoveryRequired);
        Check(r.F.Count("PeerRecoveryCompletionReceipts")==0&&r.F.Count("AuditEvents")==auditCount+1);
        Check(before.Split('\n').Where(s=>!s.StartsWith("AuditEvents")).SequenceEqual(r.Snapshot().Split('\n').Where(s=>!s.StartsWith("AuditEvents"))));
        ReceiveRecovery(r,proof,approval);
        var next=new string('E',64);
        r.F.Execute($"INSERT INTO SecureCredentialReferences (CredentialRef,Purpose,CreatedUtc,PublicKeyFingerprint) VALUES ('second-recovery','HostTlsV1','{r.F.Time.Now:O}','{next}');");
        new HostCredentialStateRepository(r.F.Database,r.F.HostId).ReplaceOffline("second-recovery",MachineCredentialRecoveryReason.CredentialLoss);
        var recovered=r.Snapshot();Reject<AuthenticationException>(()=>ReceiveRecovery(r,proof,approval));Check(recovered==r.Snapshot());
        var actual=r.Peer with{LocalFingerprint=next};
        Check(ReceiveRecovery(r,actual,approval).Disposition==RecoveryCompletionDisposition.KeyMismatch&&r.F.Repository.Read(r.F.PeerId)!.RecoveryRequired);
        Check(ReceiveRecovery(r,actual,Guid.NewGuid(),next).RecoveryCleared);
        using var fault=new Rig(false);RecoveryPending(fault);
        fault.F.Execute("CREATE TRIGGER MismatchMustNotUnlock AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PeerRecoveryCompletionKeyMismatch' BEGIN UPDATE TrustedManagers SET PeerRecoveryRequired=0 WHERE PeerHostId=NEW.ActorPeerHostId; END;");
        var untouched=fault.Snapshot();
        Reject<StaleAuthorizationRevisionException>(()=>ReceiveRecovery(fault,fault.Peer,Guid.NewGuid(),new('F',64)));
        Check(untouched==fault.Snapshot());
        return Task.CompletedTask;
    }
    public static Task RecoveryReceiptRefusesChangedProofAndRelationship()
    {
        foreach(var kind in Enumerable.Range(0,9))
        {
            using var r=new Rig(false);RecoveryPending(r);var proof=r.Peer;
            if(kind==0)proof=proof with{HostId=Guid.NewGuid()};
            if(kind==1)proof=proof with{PeerHostId=Guid.NewGuid()};
            if(kind==2)proof=proof with{PeerFingerprint=new('F',64)};
            if(kind==3)proof=proof with{LocalFingerprint=new('F',64)};
            if(kind==4)proof=proof with{Incarnation=proof.Incarnation+1};
            if(kind==5)r.Revoke();
            if(kind==6)r.F.Execute($"UPDATE TrustedManagers SET State='PeerBound' WHERE PeerHostId='{r.F.PeerId:D}';");
            if(kind==7)r.F.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=0 WHERE PeerHostId='{r.F.PeerId:D}'; UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{r.F.PeerId:D}';");
            if(kind==8)proof=proof with{PeerHostId=r.F.HostId};
            var snapshot=r.Snapshot();Reject<AuthenticationException>(()=>ReceiveRecovery(r,proof,Guid.NewGuid()));Check(snapshot==r.Snapshot());
        }
        using var revoked=new Rig(false);RecoveryPending(revoked);var original=revoked.Peer;var id=Guid.NewGuid();ReceiveRecovery(revoked,original,id);revoked.Revoke();
        Check(revoked.F.Count("PeerRecoveryCompletionReceipts")==0);Reject<AuthenticationException>(()=>ReceiveRecovery(revoked,original,id));
        return Task.CompletedTask;
    }
    public static Task RecoveryReceiptPreservesOldAndStagedNewRotation()
    {
        foreach(var pendingKey in new[]{false,true})
        {
            using var r=new Rig(false);RecoveryPending(r);var staged=new string('F',64);var rotation=Guid.NewGuid();
            r.F.Execute($"UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint='{staged}',PendingRotationId='{rotation:D}',PendingRotationExpiresUtc='{r.F.Time.Now.AddMinutes(-1):O}',PendingReconfirmationRequired=1 WHERE PeerHostId='{r.F.PeerId:D}';");
            var trust=r.F.Repository.Read(r.F.PeerId)!;var proof=r.Peer with{PeerFingerprint=pendingKey?staged:new('B',64)};
            var result=ReceiveRecovery(r,proof,Guid.NewGuid());var after=r.F.Repository.Read(r.F.PeerId)!;
            Check(result.RecoveryCleared&&after==(trust with{RecoveryRequired=false}));
            Reject<AuthenticationException>(()=>r.Repo.RequireRemoteHostCapability(proof,HostCapability.CreateServer,r.F.HostId));
            var current=proof with{Incarnation=result.Incarnation};
            var observed=r.F.Repository.ObserveActivePeerCredential(current);
            Check(observed.Promoted==pendingKey&&observed.Trust.CurrentFingerprint==(pendingKey?staged:new('B',64)));
            if(!pendingKey)Check(observed.Trust.PendingReconfirmationRequired&&observed.Trust.PendingFingerprint==staged);
        }
        return Task.CompletedTask;
    }
    public static Task RecoveryReceiptCarriesOnlyExactOutgoingApproval()
    {
        foreach(var kind in Enumerable.Range(0,4))
        {
            using var r=new Rig(false);ReplacementDefaults(r);RecoveryPending(r);
            var replaced=r.Repo.ApprovePeerReplacement(r.Owner,r.Revision,ReplacementRequest(r));var source=r.Incarnation;
            if(kind==1)r.F.Execute("UPDATE PeerReplacementCompletions SET InvalidatedUtc='fixture';");
            if(kind==2)r.F.Execute("UPDATE PeerReplacementCompletions SET CurrentIncarnation=CurrentIncarnation+100;");
            if(kind==3)r.F.Execute($"UPDATE PeerReplacementCompletions SET ApprovedPeerFingerprint='{new string('F',64)}';");
            var approval=r.F.Repository.Read(r.F.PeerId)!;var proof=r.Peer with{PeerFingerprint=ReplacementPin};
            var result=ReceiveRecovery(r,proof,Guid.NewGuid());
            Check(result.RecoveryCleared&&r.F.Repository.Read(r.F.PeerId)==(approval with{RecoveryRequired=false}));
            var markerInc=HostDatabase.QueryScalarLong(r.F.Writer,"SELECT CurrentIncarnation FROM PeerReplacementCompletions;");
            Check(markerInc==(kind==0?result.Incarnation:source+(kind==2?100:0)));
            Check(HostDatabase.QueryScalarLong(r.F.Writer,$"SELECT COUNT(*) FROM PeerReplacementCompletions WHERE ApprovalIncarnation={replaced.Incarnation} AND LocalFingerprintAtApproval='{RecoveryLocalPin}';")==1);
            Check(r.Repo.Read().HostGrants.Count==1&&r.Repo.Read().ServerGrants.Count==1);
        }
        return Task.CompletedTask;
    }
    public static Task RecoveryReceiptLateEffectsAndAuditsRollback()
    {
        foreach(var fault in new[]{
            "DELETE FROM PeerRecoveryCompletionReceipts;","UPDATE PeerRecoveryCompletionReceipts SET ApprovalId='wrong';",
            $"UPDATE PeerRecoveryCompletionReceipts SET LocalFingerprint='{new string('F',64)}';",
            "UPDATE PeerRecoveryCompletionReceipts SET SourceIncarnation=ResultIncarnation;",
            "UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId=NEW.ActorPeerHostId;",
            "UPDATE PeerReplacementCompletions SET CurrentIncarnation=CurrentIncarnation+1;",
            "UPDATE PendingCredentialReplacements SET ApprovedUtc='wrong';",
            "DELETE FROM PeerLocalBindingEvidence;","DELETE FROM HostCapabilityGrants;",
            "UPDATE HostDefaultGrants SET CanDelegate=1;","UPDATE LocalPrincipals SET IsOwner=0 WHERE IsOwner=1;",
            "UPDATE AuditEvents SET Summary='wrong' WHERE AuditEventId=NEW.AuditEventId;",
            "UPDATE AuditEvents SET ActorPeerHostId=NULL,ActorKind=NULL WHERE AuditEventId=NEW.AuditEventId;",
            "DELETE FROM AuditEvents WHERE AuditEventId=NEW.AuditEventId;"
        })
        {
            using var r=new Rig(false);ReplacementDefaults(r);RecoveryPending(r);r.Repo.ApprovePeerReplacement(r.Owner,r.Revision,ReplacementRequest(r));
            r.F.Execute($"CREATE TRIGGER RecoveryReceiptFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PeerRecoveryCompletionReceived' BEGIN {fault} END;");
            var snapshot=r.Snapshot();var refused=false;
            try{ReceiveRecovery(r,r.Peer with{PeerFingerprint=ReplacementPin},Guid.NewGuid());}
            catch(Exception ex)when(ex is InvalidOperationException or InvalidDataException or AuthenticationException or StaleAuthorizationRevisionException){refused=true;}
            if(!refused||snapshot!=r.Snapshot())throw new Exception("Recovery receipt fault did not roll back: "+fault);
        }
        return Task.CompletedTask;
    }
    public static Task RecoveryReceiptConcurrencyAndCancellation()
    {
        using var r=new Rig(false);RecoveryPending(r);var proof=r.Peer;var approval=Guid.NewGuid();var snapshot=r.Snapshot();
        using(var cancel=new CancellationTokenSource())
        {
            var repo=new GrantPolicyRepository(r.F.Database,r.F.HostId,new CancelClock(cancel,r.F.Time.Now));
            Reject<OperationCanceledException>(()=>repo.ReceiveAuthenticatedRecoveryCompletion(proof,approval,RecoveryLocalPin,cancel.Token));Check(snapshot==r.Snapshot());
        }
        var recorded=0;var duplicate=0;
        Parallel.For(0,8,_=>{var result=ReceiveRecovery(r,proof,approval);if(result.Disposition==RecoveryCompletionDisposition.Recorded)Interlocked.Increment(ref recorded);else if(result.Disposition==RecoveryCompletionDisposition.AlreadyRecorded)Interlocked.Increment(ref duplicate);});
        Check(recorded==1&&duplicate==7&&r.F.Count("PeerRecoveryCompletionReceipts")==1);
        return Task.CompletedTask;
    }
    public static Task RecoveryReceiptSchemaAddsNoAuthority()
    {
        using var f=new PeerTrustTests.Fixture(schemaVersion:15);f.Repository.RecordVerifiedBinding(f.PeerId,new('B',64),RecoveryLocalPin);
        f.Execute("UPDATE TrustedManagers SET State='Active',PeerRecoveryRequired=1;");
        var before=f.Repository.Read(f.PeerId);var revision=new GrantPolicyRepository(f.Database,f.HostId,f.Time).Read().Revision;var audits=f.Count("AuditEvents");
        var runner=new HostSchemaMigrationRunner(HostSchema.AllMigrations().Where(m=>m.Version<=16));
        Check(runner.Migrate(f.Writer)==1&&runner.Migrate(f.Writer)==0&&f.Count("PeerRecoveryCompletionReceipts")==0);
        Check(f.Repository.Read(f.PeerId)==before&&new GrantPolicyRepository(f.Database,f.HostId,f.Time).Read().Revision==revision&&f.Count("AuditEvents")==audits&&f.Count("HostCapabilityGrants")==0);
        var other=Guid.NewGuid();f.Repository.RecordVerifiedBinding(other,new('C',64),RecoveryLocalPin);
        var foreign=HostDatabase.QueryScalarLong(f.Writer,$"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{other:D}';");
        Reject<SqliteException>(()=>f.Execute($"INSERT INTO PeerRecoveryCompletionReceipts VALUES ('{f.PeerId:D}','{Guid.NewGuid():D}',1,{foreign},'{new string('B',64)}','{RecoveryLocalPin}','{f.Time.Now:O}');"));
        Check(f.Count("PeerRecoveryCompletionReceipts")==0);
        return Task.CompletedTask;
    }
}
