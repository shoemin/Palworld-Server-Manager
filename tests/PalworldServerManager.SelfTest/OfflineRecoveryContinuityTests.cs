using System.Security.Authentication;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static partial class PeerTrustRevocationTests
{
    private static void PlannedRecovery(Rig r,string reference,char key)=>r.F.Execute(
        $"INSERT INTO SecureCredentialReferences (CredentialRef,Purpose,CreatedUtc,PublicKeyFingerprint) VALUES ('{reference}','HostTlsV1','{r.F.Time.Now:O}','{new string(key,64)}');");
    public static Task OfflineRecoveryAuditDeletionRollsBack()
    {
        using var r=new Rig(false);PlannedRecovery(r,"audit-regression",'E');
        r.F.Execute("CREATE TRIGGER RemoveRecoveryAudit AFTER INSERT ON AuditEvents WHEN NEW.EventKind='HostCredentialRecoveredFromLoss' BEGIN DELETE FROM AuditEvents WHERE AuditEventId=NEW.AuditEventId; END;");
        var snapshot=r.Snapshot();var refused=false;
        var repository=new HostCredentialStateRepository(r.F.Database,r.F.HostId);
        try{repository.ReplaceOffline("audit-regression",MachineCredentialRecoveryReason.CredentialLoss);}
        catch(InvalidOperationException){refused=true;}
        if(!refused)throw new Exception($"Offline recovery committed without its final audit: CurrentReference={repository.Read().CurrentReference}; AuditCount={HostDatabase.QueryScalarLong(r.F.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostCredentialRecoveredFromLoss';")}");
        Check(snapshot==r.Snapshot());return Task.CompletedTask;
    }
    private static string MarkerRows(Rig r)=>string.Join('\n',r.Snapshot().Split('\n').Where(s=>s.StartsWith("PeerReplacementCompletions")));
    public static Task OfflineRecoveryCarriesExactApprovalThroughRepeatedRecovery()
    {
        foreach(var alreadyPending in new[]{false,true})
        {
            using var r=new Rig(false);ReplacementDefaults(r);if(alreadyPending)RecoveryPending(r);
            var id=ReplacementRequest(r);var approved=r.Repo.ApprovePeerReplacement(r.Owner,r.Revision,id);var original=r.Peer with{PeerFingerprint=ReplacementPin};
            var grants=r.Repo.Read();r.F.Repository.RecordOwnerVerifiedBinding(r.Owner,r.F.PeerId,new('F',64),RecoveryLocalPin);
            PlannedRecovery(r,"continuity-first",'E');var repository=new HostCredentialStateRepository(r.F.Database,r.F.HostId);
            repository.ReplaceOffline("continuity-first",MachineCredentialRecoveryReason.CredentialLoss);
            var first=r.Incarnation;
            Check(alreadyPending?first==approved.Incarnation:first>approved.Incarnation);
            Check(r.F.Repository.Read(r.F.PeerId)!.RecoveryRequired&&r.Repo.Read().HostGrants.SequenceEqual(grants.HostGrants)&&r.Repo.Read().ServerGrants.SequenceEqual(grants.ServerGrants));
            Check(HostDatabase.QueryScalarLong(r.F.Writer,$"SELECT COUNT(*) FROM PeerReplacementCompletions WHERE ReplacementId='{id:D}' AND CurrentIncarnation={first} AND ApprovalIncarnation={approved.Incarnation} AND LocalFingerprintAtApproval='{RecoveryLocalPin}' AND InvalidatedUtc IS NULL;")==1);
            Check(HostDatabase.QueryScalarLong(r.F.Writer,"SELECT COUNT(*) FROM PendingCredentialReplacements WHERE InvalidatedUtc=(SELECT ActivatedUtc FROM SecureCredentialReferences WHERE CredentialRef='continuity-first');")==2);
            Reject<AuthenticationException>(()=>ReceiveRecovery(r,original,Guid.NewGuid()));
            var actual=r.Peer with{PeerFingerprint=ReplacementPin,LocalFingerprint=new('E',64)};
            Check(ReceiveRecovery(r,actual,Guid.NewGuid()).Disposition==RecoveryCompletionDisposition.KeyMismatch&&r.F.Repository.Read(r.F.PeerId)!.RecoveryRequired);
            Check(ReceiveRecovery(r,actual,Guid.NewGuid(),new('E',64)).RecoveryCleared);
            Check(HostDatabase.QueryScalarLong(r.F.Writer,"SELECT CurrentIncarnation FROM PeerReplacementCompletions;")==r.Incarnation);
            PlannedRecovery(r,"continuity-second",'F');repository.ReplaceOffline("continuity-second",MachineCredentialRecoveryReason.SuspectedCompromise);
            Check(r.F.Repository.Read(r.F.PeerId)!.RecoveryRequired&&HostDatabase.QueryScalarLong(r.F.Writer,"SELECT CurrentIncarnation FROM PeerReplacementCompletions;")==r.Incarnation);
            Check(HostDatabase.QueryScalarLong(r.F.Writer,$"SELECT COUNT(*) FROM PeerReplacementCompletions WHERE ApprovalIncarnation={approved.Incarnation} AND LocalFingerprintAtApproval='{RecoveryLocalPin}' AND ConfirmedUtc IS NULL AND InvalidatedUtc IS NULL;")==1);
            Reject<AuthenticationException>(()=>ReceiveRecovery(r,actual,Guid.NewGuid(),new('E',64)));
        }
        return Task.CompletedTask;
    }
    public static Task OfflineRecoveryNeverRevivesIneligibleMarkers()
    {
        foreach(var kind in Enumerable.Range(0,5))
        {
            using var r=new Rig(false);r.Repo.ApprovePeerReplacement(r.Owner,r.Revision,ReplacementRequest(r));
            if(kind==0)r.F.Execute("UPDATE PeerReplacementCompletions SET InvalidatedUtc='fixture';");
            if(kind==1)r.F.Execute("UPDATE PeerReplacementCompletions SET CurrentIncarnation=CurrentIncarnation+100;");
            if(kind==2)r.F.Execute($"UPDATE PeerReplacementCompletions SET ApprovedPeerFingerprint='{new string('F',64)}';");
            if(kind==3)r.Revoke();
            if(kind==4)r.F.Execute($"UPDATE TrustedManagers SET State='PeerBound' WHERE PeerHostId='{r.F.PeerId:D}'; UPDATE PeerReplacementCompletions SET InvalidatedUtc=NULL,CurrentIncarnation=(SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{r.F.PeerId:D}');");
            var marker=MarkerRows(r);PlannedRecovery(r,"ineligible",'E');
            new HostCredentialStateRepository(r.F.Database,r.F.HostId).ReplaceOffline("ineligible",MachineCredentialRecoveryReason.CredentialLoss);
            Check(marker==MarkerRows(r));
        }
        return Task.CompletedTask;
    }
    public static Task OfflineRecoveryContinuityStillCancelsOnRevokeAndReplacement()
    {
        using var r=new Rig(false);ReplacementDefaults(r);var id=ReplacementRequest(r);r.Repo.ApprovePeerReplacement(r.Owner,r.Revision,id);
        PlannedRecovery(r,"before-revoke",'E');new HostCredentialStateRepository(r.F.Database,r.F.HostId).ReplaceOffline("before-revoke",MachineCredentialRecoveryReason.CredentialLoss);
        r.Revoke();Check(HostDatabase.QueryScalarLong(r.F.Writer,"SELECT COUNT(*) FROM PeerReplacementCompletions WHERE InvalidatedUtc IS NOT NULL;")==1);
        var next=r.F.Repository.RecordOwnerVerifiedBinding(r.Owner,r.F.PeerId,new('F',64),new('E',64)).ReplacementId!.Value;
        r.Repo.ApprovePeerReplacement(r.Owner,r.Revision,next);
        Check(next!=id&&HostDatabase.QueryScalarLong(r.F.Writer,$"SELECT COUNT(*) FROM PeerReplacementCompletions WHERE ReplacementId='{next:D}' AND LocalFingerprintAtApproval='{new string('E',64)}' AND InvalidatedUtc IS NULL;")==1);
        Check(r.Repo.Read().HostGrants.Count(g=>g.InvalidatedUtc is null)==1&&r.Repo.Read().HostGrants.Count(g=>g.InvalidatedUtc is not null)==1);
        return Task.CompletedTask;
    }
    public static Task OfflineRecoveryLateEffectsRollBackWholeWriter()
    {
        foreach(var fault in new[]{
            "DELETE FROM AuditEvents WHERE AuditEventId=NEW.AuditEventId;","UPDATE AuditEvents SET Summary='wrong' WHERE AuditEventId=NEW.AuditEventId;",
            "UPDATE AuditEvents SET ActorKind=NULL WHERE AuditEventId=NEW.AuditEventId;",
            $"UPDATE AuditEvents SET AffectedHostId='{Guid.NewGuid():D}' WHERE AuditEventId=NEW.AuditEventId;",
            "UPDATE AuditEvents SET IsOfflineRecovery=0 WHERE AuditEventId=NEW.AuditEventId;","UPDATE AuditEvents SET OccurredUtc='wrong' WHERE AuditEventId=NEW.AuditEventId;",
            "UPDATE PeerReplacementCompletions SET CurrentIncarnation=CurrentIncarnation+1;",
            $"UPDATE PeerReplacementCompletions SET ApprovedPeerFingerprint='{new string('F',64)}';",
            $"UPDATE PeerReplacementCompletions SET LocalFingerprintAtApproval='{new string('F',64)}';","DELETE FROM PeerReplacementCompletions;",
            "UPDATE HostIdentity SET CurrentCredentialRef='current';","UPDATE SecureCredentialReferences SET ActivatedUtc='wrong' WHERE CredentialRef='fault-recovery';",
            $"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{new string('B',64)}' WHERE CredentialRef='fault-recovery';",
            "UPDATE TrustedManagers SET PeerRecoveryRequired=0 WHERE State='Active';","UPDATE LocalPrincipals SET IsOwner=0 WHERE IsOwner=1;",
            "UPDATE HostCapabilityGrants SET CanDelegate=1;","DELETE FROM ServerCapabilityGrants;",
            "UPDATE HostCredentialRotations SET State='Prepared';","UPDATE PendingCredentialReplacements SET InvalidatedUtc=NULL;",
            "DELETE FROM PeerLocalBindingEvidence;","UPDATE HostDefaultGrants SET CanDelegate=1;",
            "DELETE FROM PeerRelationshipIncarnations WHERE PeerHostId NOT IN (SELECT PeerHostId FROM PeerReplacementCompletions);"
        })
        {
            using var r=new Rig(false);ReplacementDefaults(r);RecoveryPending(r);r.Repo.ApprovePeerReplacement(r.Owner,r.Revision,ReplacementRequest(r));
            PlannedRecovery(r,"fault-recovery",'E');PlannedRecovery(r,"rotation-next",'F');
            r.F.Execute($"INSERT INTO HostCredentialRotations (RotationId,OldCredentialRef,NewCredentialRef,State,StartedUtc) VALUES ('{Guid.NewGuid():D}','current','rotation-next','ReadyForCutover','{r.F.Time.Now:O}');");
            r.F.Execute($"CREATE TRIGGER OfflineRecoveryFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='HostCredentialRecoveredFromLoss' BEGIN {fault} END;");
            var snapshot=r.Snapshot();var refused=false;
            try{new HostCredentialStateRepository(r.F.Database,r.F.HostId).ReplaceOffline("fault-recovery",MachineCredentialRecoveryReason.CredentialLoss);}
            catch(InvalidOperationException){refused=true;}
            if(!refused||snapshot!=r.Snapshot())throw new Exception("Offline recovery fault did not roll back: "+fault);
        }
        return Task.CompletedTask;
    }
}
