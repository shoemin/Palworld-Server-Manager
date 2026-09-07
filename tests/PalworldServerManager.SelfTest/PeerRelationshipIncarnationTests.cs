using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;

namespace PalworldServerManager.SelfTest;

internal static class PeerRelationshipIncarnationTests
{
    private static readonly string Local = new('A',64), Peer = new('B',64), Next = new('C',64), PeerNext = new('D',64);
    private static void Check(bool value) { if (!value) throw new Exception("Relationship provenance assertion failed."); }
    private static void Reject<T>(Action action) where T:Exception
    { try { action(); } catch(T) { return; } throw new Exception("Expected relationship refusal: "+typeof(T).Name); }
    private static long Version(PeerTrustTests.Fixture f) => HostDatabase.QueryScalarLong(f.Writer,
        $"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{f.PeerId:D}';");
    private static HostCredentialStateRepository State(PeerTrustTests.Fixture f) => new(f.Database,f.HostId);
    private static void Bind(PeerTrustTests.Fixture f)
    {
        f.Repository.RecordVerifiedBinding(f.PeerId,Peer,Local);
        f.Execute("CREATE TABLE ActivationRpcEffects (Peer TEXT PRIMARY KEY);");
    }
    private static void Activate(PeerTrustTests.Fixture f) => f.Repository.AcceptActivationAcknowledgement(f.PeerId,Peer,Local,
        new(f.PeerId,f.HostId,Local),new LocalOwnerActivationTests.Hook());
    private static RoutineRotationPromotionReceipt CutOver(PeerTrustTests.Fixture f)
    {
        var owner=new LocalPrincipalMutationActor(f.HostId,f.OwnerId,"native-owner","fixture-public");
        var state=State(f);var r=state.PrepareRoutineRotation(owner,Guid.NewGuid());
        state.RecordCreated(r.NewReference,Next);state.BeginRoutineRotationStaging(owner,r.RotationId);
        // Explicit synthetic global cutover; these scenarios qualify receipt persistence only.
        f.Execute($"UPDATE HostIdentity SET CurrentCredentialRef='{r.NewReference}'; UPDATE HostCredentialRotations SET State='CutOver' WHERE RotationId='{r.RotationId:D}';");
        return new(Guid.NewGuid(),f.HostId,r.RotationId,Next);
    }
    private static long Evidence(PeerTrustTests.Fixture f) => HostDatabase.QueryScalarLong(f.Writer,"SELECT Incarnation FROM HostRotationPromotionEvidence;");
    public static Task CurrentConfirmationUpgradeCancellationAndFirstNewPairing()
    {
        using var f=new PeerTrustTests.Fixture(schemaVersion:6);Bind(f);Activate(f);var receipt=CutOver(f);
        State(f).RecordRoutineRotationPromotionReceipt(receipt,f.PeerId,Peer,Next,Version(f));
        var first=HostDatabase.QueryScalarText(f.Writer,"SELECT PromotedUtc FROM HostCredentialRotationPeers;");var evidence=Evidence(f);
        Check(HostSchemaMigrationRunner.Default().Migrate(f.Writer)==1 && HostSchemaMigrationRunner.Default().Migrate(f.Writer)==0);
        Check(f.Count("HostRotationCurrentCredentialEvidence")==0 && Evidence(f)==evidence);
        var proof=State(f).PrepareCurrentCredentialConfirmation(receipt.RotationId,Next);
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        Reject<OperationCanceledException>(()=>State(f).RecordCurrentCredentialConfirmation(proof,f.PeerId,Peer,Next,Version(f),cancellation.Token));
        Check(f.Count("HostRotationCurrentCredentialEvidence")==0);
        Check(State(f).RecordCurrentCredentialConfirmation(proof,f.PeerId,Peer,Next,Version(f)));
        Check(HostDatabase.QueryScalarText(f.Writer,"SELECT PromotedUtc FROM HostCredentialRotationPeers;")==first);
        // Repository seam with verified first binding under New: this peer has never promoted Old.
        var freshPeer=Guid.NewGuid();f.Repository.RecordVerifiedBinding(freshPeer,Peer,Next);
        f.Repository.AcceptActivationAcknowledgement(freshPeer,Peer,Next,new(freshPeer,f.HostId,Next),new LocalOwnerActivationTests.Hook());
        var fresh=f.Repository.ReadAuthenticatedRelationshipIncarnation(freshPeer,Peer,Next);
        Check(State(f).RecordCurrentCredentialConfirmation(proof,freshPeer,Peer,Next,fresh));
        Check(HostDatabase.QueryScalarLong(f.Writer,$"SELECT COUNT(*) FROM HostCredentialRotationPeers WHERE PeerHostId='{freshPeer:D}' AND StagedUtc IS NULL AND AcknowledgedUtc IS NULL AND PromotedUtc IS NULL;")==1);
        Check(f.Count("HostCapabilityGrants")==0 && f.Count("ServerCapabilityGrants")==0);
        return Task.CompletedTask;
    }
    public static Task ConservativeUpgradeAndMigrationRollback()
    {
        using(var f=new PeerTrustTests.Fixture(schemaVersion:5))
        {
            Bind(f);Activate(f);var receipt=CutOver(f);
            f.Execute($"INSERT INTO HostCredentialRotationPeers VALUES ('{receipt.RotationId:D}','{f.PeerId:D}','staged','acknowledged','first-promotion');");
            var trust=f.Repository.Read(f.PeerId);var audits=f.Count("AuditEvents");
            var throughIncarnation=new HostSchemaMigrationRunner(HostSchema.AllMigrations().Take(6));
            Check(throughIncarnation.Migrate(f.Writer)==1 && throughIncarnation.Migrate(f.Writer)==0);
            Check(Version(f)>0 && f.Count("HostRotationPromotionEvidence")==0 && f.Repository.Read(f.PeerId)==trust && f.Count("AuditEvents")==audits);
            Check(HostDatabase.QueryScalarText(f.Writer,"SELECT PromotedUtc FROM HostCredentialRotationPeers;")=="first-promotion");
            Check(State(f).RecordRoutineRotationPromotionReceipt(receipt,f.PeerId,Peer,Next,Version(f)));
            Check(Evidence(f)==Version(f) && HostDatabase.QueryScalarText(f.Writer,"SELECT StagedUtc||'/'||AcknowledgedUtc||'/'||PromotedUtc FROM HostCredentialRotationPeers;")=="staged/acknowledged/first-promotion");
            Check(!State(f).RecordRoutineRotationPromotionReceipt(receipt,f.PeerId,Peer,Next,Version(f)));
        }
        using(var f=new PeerTrustTests.Fixture(schemaVersion:5))
        {
            f.Execute("CREATE TABLE HostRotationPromotionEvidence (Fixture INTEGER);");
            Reject<SqliteException>(()=>HostSchemaMigrationRunner.Default().Migrate(f.Writer));
            Check(HostSchemaMigrationRunner.ReadSchemaVersion(f.Writer)==5 && HostDatabase.QueryScalarLong(f.Writer,
                "SELECT COUNT(*) FROM sqlite_master WHERE name='PeerRelationshipIncarnations';")==0);
        }
        return Task.CompletedTask;
    }
    public static Task ActivationRoutinePromotionAndUnrelatedPeersPreserveEvidence()
    {
        using var f=new PeerTrustTests.Fixture();Bind(f);var version=Version(f);Activate(f);Check(Version(f)==version);
        var receipt=CutOver(f);Check(State(f).RecordRoutineRotationPromotionReceipt(receipt,f.PeerId,Peer,Next,version));
        var proposal=new HostRotationProposal(f.PeerId,Guid.NewGuid(),1,Peer,PeerNext);
        f.Repository.StagePeerRotation(proposal,Peer,Next);Check(Version(f)==version);
        Check(f.Repository.ObserveActivePeerCredential(f.PeerId,PeerNext).Promoted && Version(f)==version);
        f.Repository.ConfirmPeerRotationReceipt(f.PeerId,PeerNext,proposal.RotationId,Next);Check(Version(f)==version && Evidence(f)==version);
        var other=Guid.NewGuid();f.Repository.RecordVerifiedBinding(other,Peer,Next);
        f.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{other:D}';");
        f.Execute($"UPDATE TrustedManagers SET DisplayName='renamed' WHERE PeerHostId='{f.PeerId:D}';");
        Check(Version(f)==version && Evidence(f)==version && f.Count("HostCapabilityGrants")==0 && f.Count("ServerCapabilityGrants")==0);
        Check(!State(f).RecordRoutineRotationPromotionReceipt(receipt,f.PeerId,PeerNext,Next,version));
        return Task.CompletedTask;
    }
    public static Task SameIdentityAbaAndPairingChangesInvalidate()
    {
        using var f=new PeerTrustTests.Fixture();Bind(f);Activate(f);var original=Version(f);var receipt=CutOver(f);
        Check(State(f).RecordRoutineRotationPromotionReceipt(receipt,f.PeerId,Peer,Next,original));
        var first=HostDatabase.QueryScalarText(f.Writer,"SELECT PromotedUtc FROM HostCredentialRotationPeers;");
        foreach(var sql in new[] {
            "UPDATE TrustedManagers SET PeerRecoveryRequired=1; UPDATE TrustedManagers SET PeerRecoveryRequired=0;",
            $"UPDATE TrustedManagers SET State='Revoked',CurrentTrustedPublicKeyFingerprint=NULL; UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='{Peer}';",
            $"UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='{PeerNext}'; UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='{Peer}';",
            "UPDATE TrustedManagerPairings SET BoundUtc=BoundUtc;",
            "UPDATE TrustedManagers SET PairedUtc='new-activation';",
            "UPDATE TrustedManagers SET CreatedUtc='new-relationship';" })
        {
            var before=Version(f);f.Execute(sql);Check(Version(f)>before && Evidence(f)!=Version(f));
            Reject<AuthenticationException>(()=>State(f).RecordRoutineRotationPromotionReceipt(receipt,f.PeerId,Peer,Next,original));
        }
        var fresh=f.Repository.ReadAuthenticatedRelationshipIncarnation(f.PeerId,Peer,Next);
        Check(State(f).RecordRoutineRotationPromotionReceipt(receipt,f.PeerId,Peer,Next,fresh) && Evidence(f)==fresh);
        Check(HostDatabase.QueryScalarText(f.Writer,"SELECT PromotedUtc FROM HostCredentialRotationPeers;")==first);
        // A delete/recreate with the identical identity and key also cannot reuse an incarnation.
        using var recreated=new PeerTrustTests.Fixture();Bind(recreated);var old=Version(recreated);
        recreated.Execute("DELETE FROM TrustedManagerPairings; DELETE FROM TrustedManagers;");
        recreated.Repository.RecordVerifiedBinding(recreated.PeerId,Peer,Local);Check(Version(recreated)>old);
        return Task.CompletedTask;
    }
    public static async Task QueuedReceiptAuditRollbackAndMetadataRefusal()
    {
        using var f=new PeerTrustTests.Fixture();Bind(f);Activate(f);var receipt=CutOver(f);var old=Version(f);
        using(var tx=f.Writer.BeginTransaction())
        {
            HostDatabase.Execute(f.Writer,"UPDATE TrustedManagers SET PeerRecoveryRequired=1; UPDATE TrustedManagers SET PeerRecoveryRequired=0;",tx);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task=Task.Run(()=>{entered.SetResult();return State(f).RecordRoutineRotationPromotionReceipt(receipt,f.PeerId,Peer,Next,old);});
            try {await entered.Task;await Task.Delay(100);Check(!task.IsCompleted);} finally {tx.Commit();}
            try {await task.WaitAsync(TimeSpan.FromSeconds(10));throw new Exception("Stale queued receipt succeeded.");} catch(AuthenticationException) { }
        }
        Check(f.Count("HostRotationPromotionEvidence")==0 && f.Count("HostCredentialRotationPeers")==0);
        var version=Version(f);
        f.Execute("CREATE TRIGGER FailProvenance BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='HostRotationPeerPromotionReceived' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        Reject<SqliteException>(()=>State(f).RecordRoutineRotationPromotionReceipt(receipt,f.PeerId,Peer,Next,version));
        Check(f.Count("HostRotationPromotionEvidence")==0 && f.Count("HostCredentialRotationPeers")==0);
        f.Execute("DROP TRIGGER FailProvenance;");
        using(var tx=f.Writer.BeginTransaction())
        {HostDatabase.Execute(f.Writer,"UPDATE TrustedManagerPairings SET BoundUtc=BoundUtc;",tx);tx.Rollback();}
        Check(Version(f)==version);
        f.Execute("UPDATE sqlite_sequence SET seq=9223372036854775807 WHERE name='PeerRelationshipIncarnations';");
        Reject<SqliteException>(()=>f.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=1;"));
        Check(Version(f)==version && !f.Repository.Read(f.PeerId)!.RecoveryRequired);
        f.Execute("DELETE FROM PeerRelationshipIncarnations;");
        Reject<InvalidDataException>(()=>f.Repository.ReadAuthenticatedRelationshipIncarnation(f.PeerId,Peer,Next));
        Reject<InvalidDataException>(()=>State(f).RecordRoutineRotationPromotionReceipt(receipt,f.PeerId,Peer,Next,version));
        Reject<SqliteException>(()=>f.Execute("UPDATE TrustedManagers SET DisplayName='unchanged';"));
        Check(f.Count("HostRotationPromotionEvidence")==0 && f.Count("HostCapabilityGrants")==0);
    }
}
