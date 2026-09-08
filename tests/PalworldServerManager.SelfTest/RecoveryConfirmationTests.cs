using System.Security.Authentication;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static partial class PeerTrustRevocationTests
{
    private static (Guid Approval,PeerGrantMutationActor Proof) ApprovedCompletion(Rig r)
    {
        var approval=ReplacementRequest(r);r.Repo.ApprovePeerReplacement(r.Owner,r.Revision,approval);
        return (approval,r.Peer with{PeerFingerprint=ReplacementPin});
    }
    public static Task ConfirmationChangesOnlyMarkerAndActualAudit()
    {
        using var r=new Rig();ReplacementDefaults(r);var (approval,proof)=ApprovedCompletion(r);
        var before=r.Repo.Read();var snapshot=r.Snapshot();var marker=MarkerRows(r);var audits=r.F.Count("AuditEvents");
        var pending=r.Repo.ReadPendingRecoveryCompletion(proof);
        Check(pending==new PendingPeerRecoveryCompletion(r.F.PeerId,approval,ReplacementPin,r.Incarnation)&&snapshot==r.Snapshot());
        var result=r.Repo.ConfirmAuthenticatedRecoveryCompletion(proof,approval);var after=r.Repo.Read();
        Check(result.Changed&&result.Revision==before.Revision&&result.Incarnation==proof.Incarnation&&result.ApprovalId==approval);
        Check(before.HostGrants.SequenceEqual(after.HostGrants)&&before.ServerGrants.SequenceEqual(after.ServerGrants));
        Check(r.F.Count("AuditEvents")==audits+1&&marker!=MarkerRows(r)&&r.Repo.ReadPendingRecoveryCompletion(proof) is null);
        Check(HostDatabase.QueryScalarLong(r.F.Writer,$"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerRecoveryCompletionConfirmed' AND ActorKind='RemoteManager' AND ActorPeerHostId='{r.F.PeerId:D}' AND ActorLocalPrincipalId IS NULL AND AffectedHostId='{r.F.HostId:D}' AND Summary NOT LIKE '%{ReplacementPin}%';")==1);
        Check(snapshot.Split('\n').Where(s=>!s.StartsWith("PeerReplacementCompletions")&&!s.StartsWith("AuditEvents")).SequenceEqual(
            r.Snapshot().Split('\n').Where(s=>!s.StartsWith("PeerReplacementCompletions")&&!s.StartsWith("AuditEvents"))));
        return Task.CompletedTask;
    }
    public static Task ConfirmationAbsentAndDuplicateAreReadOnly()
    {
        using var r=new Rig(false);var before=r.Snapshot();Check(r.Repo.ReadPendingRecoveryCompletion(r.Peer) is null&&before==r.Snapshot());
        Reject<AuthenticationException>(()=>r.Repo.ConfirmAuthenticatedRecoveryCompletion(r.Peer,Guid.NewGuid()));Check(before==r.Snapshot());
        var (approval,proof)=ApprovedCompletion(r);r.Repo.ConfirmAuthenticatedRecoveryCompletion(proof,approval);before=r.Snapshot();
        r.F.Time.Now+=TimeSpan.FromDays(1);
        Check(!r.Repo.ConfirmAuthenticatedRecoveryCompletion(proof,approval).Changed&&r.Repo.ReadPendingRecoveryCompletion(proof) is null&&before==r.Snapshot());
        Reject<AuthenticationException>(()=>r.Repo.ConfirmAuthenticatedRecoveryCompletion(proof,Guid.NewGuid()));Check(before==r.Snapshot());
        return Task.CompletedTask;
    }
    public static Task ConfirmationUsesCurrentLocalKeyAfterOfflineRecovery()
    {
        foreach(var alreadyRecovery in new[]{false,true})
        {
            using var r=new Rig(false);if(alreadyRecovery)RecoveryPending(r);var (approval,original)=ApprovedCompletion(r);
            PlannedRecovery(r,"confirmation-own-recovery",'E');new HostCredentialStateRepository(r.F.Database,r.F.HostId).ReplaceOffline("confirmation-own-recovery",MachineCredentialRecoveryReason.CredentialLoss);
            var proof=original with{LocalFingerprint=new('E',64),Incarnation=r.Incarnation};var snapshot=r.Snapshot();
            Reject<AuthenticationException>(()=>r.Repo.ReadPendingRecoveryCompletion(original));
            Reject<AuthenticationException>(()=>r.Repo.ConfirmAuthenticatedRecoveryCompletion(original,approval));Check(snapshot==r.Snapshot());
            Check(r.Repo.ReadPendingRecoveryCompletion(proof)!.ApprovalId==approval);
            Check(r.Repo.ConfirmAuthenticatedRecoveryCompletion(proof,approval).Changed&&r.F.Repository.Read(r.F.PeerId)!.RecoveryRequired);
            Check(HostDatabase.QueryScalarLong(r.F.Writer,$"SELECT COUNT(*) FROM PeerReplacementCompletions WHERE LocalFingerprintAtApproval='{new string('A',64)}' AND ConfirmedUtc IS NOT NULL;")==1);
            Check(HostDatabase.QueryScalarLong(r.F.Writer,"SELECT COUNT(*) FROM PendingCredentialReplacements WHERE ApprovedUtc IS NOT NULL AND InvalidatedUtc IS NOT NULL;")==1);
            Reject<AuthenticationException>(()=>r.Repo.RequireRemoteHostCapability(proof,HostCapability.CreateServer,r.F.HostId));
        }
        return Task.CompletedTask;
    }
    public static Task ConfirmationRechecksIncomingReceiptIncarnation()
    {
        using var r=new Rig(false);RecoveryPending(r);var (approval,old)=ApprovedCompletion(r);
        var pending=r.Repo.ReadPendingRecoveryCompletion(old)!;
        r.Repo.ReceiveAuthenticatedRecoveryCompletion(old,Guid.NewGuid(),new('A',64));var snapshot=r.Snapshot();
        Reject<AuthenticationException>(()=>r.Repo.ConfirmAuthenticatedRecoveryCompletion(old,pending.ApprovalId));Check(snapshot==r.Snapshot());
        var current=old with{Incarnation=r.Incarnation};var carried=r.Repo.ReadPendingRecoveryCompletion(current)!;
        Check(carried.ApprovalId==pending.ApprovalId&&carried.Incarnation>pending.Incarnation);
        Check(r.Repo.ConfirmAuthenticatedRecoveryCompletion(current,approval).Changed&&!r.F.Repository.Read(r.F.PeerId)!.RecoveryRequired);
        return Task.CompletedTask;
    }
    public static Task ConfirmationRefusesStaleProofAndUnapprovedMarkers()
    {
        foreach(var mode in Enumerable.Range(0,13))
        {
            using var r=new Rig(false);var (approval,proof)=ApprovedCompletion(r);
            if(mode==0)proof=proof with{HostId=Guid.NewGuid()};if(mode==1)proof=proof with{PeerHostId=r.Other};
            if(mode==2)proof=proof with{PeerFingerprint=new('B',64)};if(mode==3)proof=proof with{LocalFingerprint=new('E',64)};
            if(mode==4)proof=proof with{Incarnation=proof.Incarnation+100};
            if(mode==5)r.Revoke();
            if(mode==6)r.F.Execute("UPDATE PeerReplacementCompletions SET InvalidatedUtc='fixture';");
            if(mode==7)r.F.Execute("UPDATE PeerReplacementCompletions SET CurrentIncarnation=CurrentIncarnation+100;");
            if(mode==8)r.F.Execute($"UPDATE PeerReplacementCompletions SET ApprovedPeerFingerprint='{new string('F',64)}';");
            if(mode==9)r.F.Execute("UPDATE PendingCredentialReplacements SET ApprovedUtc=NULL,ApprovedByOwnerLocalPrincipalId=NULL;");
            if(mode==10)r.F.Execute("DELETE FROM PeerReplacementBindingEvidence;");
            if(mode==11)r.F.Execute($"UPDATE PeerReplacementBindingEvidence SET LocalFingerprint='{new string('F',64)}';");
            if(mode==12)r.F.Execute($"UPDATE PendingCredentialReplacements SET ProposedKeyFingerprint='{new string('F',64)}';");
            var snapshot=r.Snapshot();Reject<AuthenticationException>(()=>r.Repo.ReadPendingRecoveryCompletion(proof));
            Reject<AuthenticationException>(()=>r.Repo.ConfirmAuthenticatedRecoveryCompletion(proof,approval));Check(snapshot==r.Snapshot());
        }
        using var superseded=new Rig();ReplacementDefaults(superseded);var (first,old)=ApprovedCompletion(superseded);
        superseded.Repo.ConfirmAuthenticatedRecoveryCompletion(old,first);superseded.Revoke();
        var nextPin=new string('F',64);var next=superseded.F.Repository.RecordOwnerVerifiedBinding(superseded.Owner,superseded.F.PeerId,nextPin,new('A',64)).ReplacementId!.Value;
        superseded.Repo.ApprovePeerReplacement(superseded.Owner,superseded.Revision,next);
        var current=superseded.Peer with{PeerFingerprint=nextPin};var state=superseded.Snapshot();
        Reject<AuthenticationException>(()=>superseded.Repo.ConfirmAuthenticatedRecoveryCompletion(current,first));
        Reject<AuthenticationException>(()=>superseded.Repo.ConfirmAuthenticatedRecoveryCompletion(old,first));Check(state==superseded.Snapshot());
        Check(superseded.Repo.ReadPendingRecoveryCompletion(current)!.ApprovalId==next&&superseded.Repo.ConfirmAuthenticatedRecoveryCompletion(current,next).Changed);
        return Task.CompletedTask;
    }
    public static Task ConfirmationNeverSubstitutesStagedPeerKey()
    {
        using var r=new Rig(false);var (approval,proof)=ApprovedCompletion(r);var staged=new string('F',64);
        r.F.Execute($"UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint='{staged}',PendingRotationId='{Guid.NewGuid():D}',PendingRotationExpiresUtc='{r.F.Time.Now.AddMinutes(-1):O}',PendingReconfirmationRequired=1 WHERE PeerHostId='{r.F.PeerId:D}';");
        var trust=r.F.Repository.Read(r.F.PeerId);var snapshot=r.Snapshot();var next=proof with{PeerFingerprint=staged};
        Reject<AuthenticationException>(()=>r.Repo.ReadPendingRecoveryCompletion(next));
        Reject<AuthenticationException>(()=>r.Repo.ConfirmAuthenticatedRecoveryCompletion(next,approval));Check(snapshot==r.Snapshot());
        Check(r.Repo.ConfirmAuthenticatedRecoveryCompletion(proof,approval).Changed&&r.F.Repository.Read(r.F.PeerId)==trust);
        r.F.Repository.ObserveActivePeerCredential(next);snapshot=r.Snapshot();
        Reject<AuthenticationException>(()=>r.Repo.ReadPendingRecoveryCompletion(next));
        Reject<AuthenticationException>(()=>r.Repo.ConfirmAuthenticatedRecoveryCompletion(next,approval));Check(snapshot==r.Snapshot());
        return Task.CompletedTask;
    }
    public static Task ConfirmationLateEffectsAndAuditRollback()
    {
        foreach(var fault in new[]{
            "DELETE FROM AuditEvents WHERE AuditEventId=NEW.AuditEventId;","UPDATE AuditEvents SET Summary='wrong' WHERE AuditEventId=NEW.AuditEventId;",
            "UPDATE AuditEvents SET ActorPeerHostId=NULL,ActorKind=NULL WHERE AuditEventId=NEW.AuditEventId;",
            "UPDATE PeerReplacementCompletions SET ConfirmedUtc=NULL;","UPDATE PeerReplacementCompletions SET ApprovedUtc='wrong';",
            "UPDATE PeerReplacementCompletions SET CurrentIncarnation=CurrentIncarnation+1;","DELETE FROM PeerReplacementCompletions;",
            "UPDATE TrustedManagers SET PeerRecoveryRequired=0;","UPDATE LocalPrincipals SET IsOwner=0 WHERE IsOwner=1;",
            "UPDATE HostCapabilityGrants SET CanDelegate=1;","DELETE FROM ServerCapabilityGrants;",
            "UPDATE HostDefaultGrants SET CanDelegate=1;","UPDATE ServerDefaultGrants SET CanDelegate=1;",
            "UPDATE PendingCredentialReplacements SET InvalidatedUtc='changed';","DELETE FROM PeerReplacementBindingEvidence;",
            $"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{new string('F',64)}' WHERE CredentialRef='current';",
            "UPDATE SecureCredentialReferences SET CreatedUtc='changed' WHERE CredentialRef='current';",
            "UPDATE PeerLocalBindingEvidence SET BoundUtc='changed';"})
        {
            using var r=new Rig(false);ReplacementDefaults(r);RecoveryPending(r);var (approval,proof)=ApprovedCompletion(r);
            r.F.Execute("CREATE TRIGGER ConfirmationFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PeerRecoveryCompletionConfirmed' BEGIN "+fault+" END;");
            var snapshot=r.Snapshot();var refused=false;
            try{r.Repo.ConfirmAuthenticatedRecoveryCompletion(proof,approval);}
            catch(Exception e)when(e is InvalidOperationException or InvalidDataException or AuthenticationException or StaleAuthorizationRevisionException or FormatException){refused=true;}
            Check(refused&&snapshot==r.Snapshot());
        }
        return Task.CompletedTask;
    }
    public static async Task ConfirmationConcurrencyAndCancellation()
    {
        using var r=new Rig(false);var (approval,proof)=ApprovedCompletion(r);using var canceled=new CancellationTokenSource();canceled.Cancel();
        var snapshot=r.Snapshot();Reject<OperationCanceledException>(()=>r.Repo.ReadPendingRecoveryCompletion(proof,canceled.Token));
        Reject<OperationCanceledException>(()=>r.Repo.ConfirmAuthenticatedRecoveryCompletion(proof,approval,canceled.Token));Check(snapshot==r.Snapshot());
        using var during=new CancellationTokenSource();var repo=new GrantPolicyRepository(r.F.Database,r.F.HostId,new CancelClock(during,r.F.Time.Now));
        Reject<OperationCanceledException>(()=>repo.ConfirmAuthenticatedRecoveryCompletion(proof,approval,during.Token));Check(snapshot==r.Snapshot());
        var results=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>r.Repo.ConfirmAuthenticatedRecoveryCompletion(proof,approval))));
        Check(results.Count(x=>x.Changed)==1&&results.Count(x=>!x.Changed)==7&&r.Repo.ReadPendingRecoveryCompletion(proof) is null);
    }
}
