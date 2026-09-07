using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static class RotationCompletionTests
{
    private static readonly string Old=new('A',64), Peer=new('B',64), New=new('C',64);
    private static void Check(bool value) {if(!value)throw new Exception("Rotation completion assertion failed.");}
    private static void Reject<T>(Action work) where T:Exception
    {try {work();}catch(T) {return;}throw new Exception("Expected completion refusal: "+typeof(T).Name);}
    private sealed class Rig:IDisposable
    {
        internal readonly PeerTrustTests.Fixture F=new();
        internal readonly Guid A=Guid.NewGuid(), B=Guid.NewGuid();
        internal HostCredentialStateRepository State=>new(F.Database,F.HostId);
        internal LocalPrincipalMutationActor Owner=>new(F.HostId,F.OwnerId,"native-owner","fixture-public");
        internal readonly RoutineRotationPreparation Rotation;
        internal Rig()
        {
            F.Execute("CREATE TABLE ActivationRpcEffects (Peer TEXT PRIMARY KEY);");Bind(A,Old);Bind(B,Old);
            Rotation=State.PrepareRoutineRotation(Owner,Guid.NewGuid());State.RecordCreated(Rotation.NewReference,New);
            State.BeginRoutineRotationStaging(Owner,Rotation.RotationId);
            // Synthetic global cutover for persistence predicate tests; actual cutover is tested in the generation suite.
            F.Execute($"UPDATE HostIdentity SET CurrentCredentialRef='{Rotation.NewReference}'; UPDATE HostCredentialRotations SET State='CutOver'; UPDATE SecureCredentialReferences SET ActivatedUtc='fixture-cutover' WHERE CredentialRef='{Rotation.NewReference}';");
        }
        internal void Bind(Guid peer,string local,bool activate=true)
        {
            F.Repository.RecordVerifiedBinding(peer,Peer,local);
            if(activate)Activate(peer,local);
        }
        internal void Activate(Guid peer,string local)=>F.Repository.AcceptActivationAcknowledgement(peer,Peer,local,
            new(peer,F.HostId,local),new LocalOwnerActivationTests.Hook());
        internal long Version(Guid peer)=>F.Repository.ReadAuthenticatedRelationshipIncarnation(peer,Peer,New);
        internal void Receipt(Guid peer)=>State.RecordRoutineRotationPromotionReceipt(new(Guid.NewGuid(),F.HostId,Rotation.RotationId,New),peer,Peer,New,Version(peer));
        internal void Confirm(Guid peer)=>State.RecordCurrentCredentialConfirmation(State.PrepareCurrentCredentialConfirmation(Rotation.RotationId,New),peer,Peer,New,Version(peer));
        internal RoutineRotationCompletionAssessment Inspect()=>State.InspectRoutineRotationCompletion(Owner,Rotation.RotationId,New);
        internal RoutineRotationPreparation Complete(CancellationToken ct=default)=>State.CommitRoutineRotationCompletionWhileQuiesced(Owner,Rotation.RotationId,New,ct);
        internal void Retained()
        {
            Check(State.Read().Rotations.Single().State==HostCredentialRotationState.CutOver);
            Check(HostTrustPlanning.Build(State.Read()).Retained.Contains(Rotation.OldReference));
            Check(F.Count("HostCapabilityGrants")==0 && F.Count("ServerCapabilityGrants")==0);
        }
        public void Dispose()=>F.Dispose();
    }
    public static Task EveryPeerMustResolveAndExpiryIsNotProof()
    {
        foreach(var revoke in new[]{false,true})
        {
            using var r=new Rig();r.Receipt(r.A);
            Check(r.Inspect().UnresolvedPeers.SequenceEqual(new[]{r.B}));Reject<AuthenticationException>(()=>r.Complete());
            r.F.Time.Now+=TimeSpan.FromDays(3650); // Sender time alone never supplies remote proof.
            Check(!r.Inspect().Ready);Reject<AuthenticationException>(()=>r.Complete());r.Retained();
            // Peer-local promotion alone/lost receipt has no Host evidence; only the durable receipt below resolves it.
            if(revoke)r.F.Execute($"UPDATE TrustedManagers SET State='Revoked',CurrentTrustedPublicKeyFingerprint=NULL,PendingTrustedPublicKeyFingerprint=NULL,PendingRotationId=NULL,PendingRotationExpiresUtc=NULL,PendingReconfirmationRequired=0,PeerRecoveryRequired=0,RevokedUtc='fixture' WHERE PeerHostId='{r.B:D}';");
            else r.Receipt(r.B);
            Check(r.Inspect().Ready && r.Complete().State==HostCredentialRotationState.Completed);
            Check(HostTrustPlanning.Build(r.State.Read()).Retire.Contains(r.Rotation.OldReference));
            var stamp=HostDatabase.QueryScalarText(r.F.Writer,"SELECT CompletedUtc FROM HostCredentialRotations;");
            Check(r.Complete().State==HostCredentialRotationState.Completed && HostDatabase.QueryScalarText(r.F.Writer,"SELECT CompletedUtc FROM HostCredentialRotations;")==stamp);
            Check(HostDatabase.QueryScalarLong(r.F.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRoutineRotationCompleted';")==1);
            Reject<AuthenticationException>(()=>r.State.CommitRoutineRotationCompletionWhileQuiesced(r.Owner with {PublicVerificationKey="stale"},r.Rotation.RotationId,New));
            Check(r.State.Read().Credentials.All(c=>!c.Retired)); // Eligibility is not actual protected deletion.
        }
        return Task.CompletedTask;
    }
    public static Task CurrentIncarnationAndFirstNewProof()
    {
        using var r=new Rig();r.Receipt(r.A);r.Confirm(r.B);Check(r.Inspect().Ready);
        r.F.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{r.A:D}'; UPDATE TrustedManagers SET PeerRecoveryRequired=0 WHERE PeerHostId='{r.A:D}';");
        Check(r.Inspect().UnresolvedPeers.SequenceEqual(new[]{r.A}));Reject<AuthenticationException>(()=>r.Complete());r.Confirm(r.A);
        var fresh=Guid.NewGuid();r.Bind(fresh,New,activate:false);Check(r.Inspect().UnresolvedPeers.SequenceEqual(new[]{fresh}));
        Reject<AuthenticationException>(()=>r.Complete());r.Activate(fresh,New);Check(r.Inspect().Ready);
        Check(HostDatabase.QueryScalarLong(r.F.Writer,$"SELECT COUNT(*) FROM HostCredentialRotationPeers WHERE PeerHostId='{fresh:D}';")==0);
        r.F.Execute($"UPDATE TrustedManagerPairings SET BoundUtc=BoundUtc WHERE PeerHostId='{fresh:D}';");
        Check(r.Inspect().UnresolvedPeers.SequenceEqual(new[]{fresh}));r.Confirm(fresh);Check(r.Inspect().Ready);
        r.F.Execute("DELETE FROM HostRotationPromotionEvidence; DELETE FROM HostRotationCurrentCredentialEvidence;");
        Check(r.Inspect().UnresolvedPeers.Count==3);Reject<AuthenticationException>(()=>r.Complete());r.Retained();
        r.Confirm(r.A);r.Confirm(r.B);r.Confirm(fresh);Check(r.Complete().State==HostCredentialRotationState.Completed);
        Check(HostDatabase.QueryScalarLong(r.F.Writer,$"SELECT COUNT(*) FROM HostCredentialRotationPeers WHERE PeerHostId='{fresh:D}' AND PromotedUtc IS NULL;")==1);
        return Task.CompletedTask;
    }
    public static Task FinalAuditAndScopeRollback()
    {
        foreach(var mode in Enumerable.Range(0,5))
        {
            using var r=new Rig();r.Receipt(r.A);r.Confirm(r.B);
            var action=mode switch {
                0=>"SELECT RAISE(ABORT,'fixture');",
                1=>"UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;",
                2=>"UPDATE TrustedManagers SET PeerRecoveryRequired=1; UPDATE TrustedManagers SET PeerRecoveryRequired=0;",
                3=>$"INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('{Guid.NewGuid():D}','Active','{Peer}','fixture');",
                _=>"DELETE FROM HostRotationCurrentCredentialEvidence;"
            };
            r.F.Execute("CREATE TRIGGER ChangeCompletion AFTER INSERT ON AuditEvents WHEN NEW.EventKind='HostRoutineRotationCompleted' BEGIN "+action+" END;");
            var refused=false;try {r.Complete();}catch(Exception ex)when(ex is AuthenticationException or SqliteException){refused=true;}
            Check(refused);r.Retained();Check(HostDatabase.QueryScalarLong(r.F.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRoutineRotationCompleted';")==0);
            r.F.Execute("DROP TRIGGER ChangeCompletion;");Check(r.Complete().State==HostCredentialRotationState.Completed);
        }
        return Task.CompletedTask;
    }
    public static async Task WriterQueueRechecksOwnerPeerAndCancellation()
    {
        foreach(var mode in Enumerable.Range(0,3))
        {
            using var r=new Rig();r.Receipt(r.A);r.Confirm(r.B);using var cancellation=new CancellationTokenSource();
            using(var tx=r.F.Writer.BeginTransaction())
            {
                if(mode==0)HostDatabase.Execute(r.F.Writer,"UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;",tx);
                if(mode==1)HostDatabase.Execute(r.F.Writer,"UPDATE TrustedManagers SET PeerRecoveryRequired=1; UPDATE TrustedManagers SET PeerRecoveryRequired=0;",tx);
                var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var work=Task.Run(()=>{entered.SetResult();return r.Complete(cancellation.Token);});
                try {await entered.Task;await Task.Delay(100);Check(!work.IsCompleted);if(mode==2)cancellation.Cancel();}finally {tx.Commit();}
                var refused=false;try {await work.WaitAsync(TimeSpan.FromSeconds(10));}
                catch(Exception ex)when(ex is AuthenticationException or OperationCanceledException){refused=true;}
                Check(refused);
            }
            r.Retained();Check(HostDatabase.QueryScalarLong(r.F.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRoutineRotationCompleted';")==0);
        }
    }
    public static Task InvalidMetadataAndCompletedScopeRefuse()
    {
        using var r=new Rig();r.Receipt(r.A);r.Confirm(r.B);
        Reject<AuthenticationException>(()=>r.State.InspectRoutineRotationCompletion(r.Owner,r.Rotation.RotationId,Old));
        Reject<AuthenticationException>(()=>r.State.InspectRoutineRotationCompletion(r.Owner,Guid.NewGuid(),New));
        using var canceled=new CancellationTokenSource();canceled.Cancel();Reject<OperationCanceledException>(()=>r.Complete(canceled.Token));
        r.F.Execute($"DELETE FROM PeerRelationshipIncarnations WHERE PeerHostId='{r.A:D}';");
        Check(r.Inspect().UnresolvedPeers.Contains(r.A));Reject<AuthenticationException>(()=>r.Complete());r.Retained();
        return Task.CompletedTask;
    }
}
