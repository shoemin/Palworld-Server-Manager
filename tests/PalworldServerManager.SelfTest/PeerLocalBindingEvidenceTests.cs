using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;

namespace PalworldServerManager.SelfTest;

internal static class PeerLocalBindingEvidenceTests
{
    private static readonly string Local=new('A',64), Peer=new('B',64), Next=new('C',64);
    private static void Check(bool value) {if(!value)throw new Exception("Local binding provenance assertion failed.");}
    private static void Reject<T>(Action action) where T:Exception
    {try {action();}catch(T) {return;}throw new Exception("Expected local binding refusal: "+typeof(T).Name);}
    private static LocalPrincipalMutationActor Owner(PeerTrustTests.Fixture f)=>new(f.HostId,f.OwnerId,"native-owner","fixture-public");
    private static long Incarnation(PeerTrustTests.Fixture f)=>HostDatabase.QueryScalarLong(f.Writer,"SELECT Incarnation FROM PeerRelationshipIncarnations;");
    private static long Evidence(PeerTrustTests.Fixture f)=>HostDatabase.QueryScalarLong(f.Writer,"SELECT Incarnation FROM PeerLocalBindingEvidence;");
    internal static void AssertActualBinding(SqliteConnection c,Guid peer,string actualLocal)
    {
        using var cmd=c.CreateCommand();cmd.CommandText="""
            SELECT COUNT(*) FROM PeerLocalBindingEvidence e JOIN PeerRelationshipIncarnations i USING(PeerHostId)
            JOIN TrustedManagerPairings p USING(PeerHostId)
            WHERE e.PeerHostId=$peer AND e.Incarnation=i.Incarnation AND e.LocalFingerprint=$local
                AND p.LocalBoundPublicKeyFingerprint=e.LocalFingerprint AND p.BoundUtc=e.BoundUtc;
            """;
        cmd.Parameters.AddWithValue("$peer",peer.ToString("D"));cmd.Parameters.AddWithValue("$local",actualLocal);
        Check(Convert.ToInt64(cmd.ExecuteScalar())==1);
    }
    public static Task ConservativeUpgradeAndRollback()
    {
        using(var f=new PeerTrustTests.Fixture(schemaVersion:7))
        {
            f.SeedHistoricalBinding(f.PeerId,Peer,Local);var trust=f.Repository.Read(f.PeerId);var version=Incarnation(f);
            var throughBinding=new HostSchemaMigrationRunner(HostSchema.AllMigrations().Take(8));
            Check(throughBinding.Migrate(f.Writer)==1 && throughBinding.Migrate(f.Writer)==0);
            Check(f.Count("PeerLocalBindingEvidence")==0 && f.Repository.Read(f.PeerId)==trust && Incarnation(f)==version);
            Check(f.Repository.RecordVerifiedBinding(f.PeerId,Peer,Local).Disposition==PeerBindingDisposition.ResumePeerBound);
            Check(f.Count("PeerLocalBindingEvidence")==0); // Resume cannot retroactively certify the first binding.
        }
        using(var f=new PeerTrustTests.Fixture(schemaVersion:7))
        {
            f.SeedHistoricalBinding(f.PeerId,Peer,Local);var version=Incarnation(f);
            f.Execute("CREATE TABLE PeerLocalBindingEvidence (Fixture INTEGER);");
            Reject<SqliteException>(()=>HostSchemaMigrationRunner.Default().Migrate(f.Writer));
            Check(HostSchemaMigrationRunner.ReadSchemaVersion(f.Writer)==7 && Incarnation(f)==version && f.Count("TrustedManagers")==1);
        }
        return Task.CompletedTask;
    }
    public static Task OwnerBindingContinuityAndInvalidation()
    {
        using var f=new PeerTrustTests.Fixture();
        Check(f.Repository.RecordOwnerVerifiedBinding(Owner(f),f.PeerId,Peer,Local).Disposition==PeerBindingDisposition.PeerBoundCreated);
        AssertActualBinding(f.Writer,f.PeerId,Local);var version=Evidence(f);
        var stamp=HostDatabase.QueryScalarText(f.Writer,"SELECT BoundUtc FROM PeerLocalBindingEvidence;");
        Check(f.Repository.RecordVerifiedBinding(f.PeerId,Peer,Local).Disposition==PeerBindingDisposition.ResumePeerBound);
        f.Execute("CREATE TABLE ActivationRpcEffects (Peer TEXT PRIMARY KEY);");
        f.Repository.AcceptActivationAcknowledgement(f.PeerId,Peer,Local,new(f.PeerId,f.HostId,Local),new LocalOwnerActivationTests.Hook());
        var proposal=new HostRotationProposal(f.PeerId,Guid.NewGuid(),1,Peer,Next);
        f.Repository.StagePeerRotation(proposal,Peer,Local);Check(f.Repository.ObserveActivePeerCredential(f.PeerId,Next).Promoted);
        Check(Evidence(f)==version && Incarnation(f)==version);
        Check(f.Repository.RecordVerifiedBinding(f.PeerId,Next,Local).Disposition==PeerBindingDisposition.ActiveReconfirmed);
        f.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=1; UPDATE TrustedManagers SET PeerRecoveryRequired=0;");
        Check(Incarnation(f)!=version && Evidence(f)==version);
        Check(f.Repository.RecordOwnerVerifiedBinding(Owner(f),f.PeerId,Next,Local).Disposition==PeerBindingDisposition.ActiveReconfirmed);
        Check(Evidence(f)==version && HostDatabase.QueryScalarText(f.Writer,"SELECT BoundUtc FROM PeerLocalBindingEvidence;")==stamp);
        Check(f.Repository.RecordVerifiedBinding(f.PeerId,Peer,Local).Disposition==PeerBindingDisposition.ReplacementRequired && Evidence(f)==version);
        f.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=1;");
        Check(f.Repository.RecordVerifiedBinding(f.PeerId,Next,Local).Disposition==PeerBindingDisposition.RecoveryRequired && Evidence(f)==version);
        Check(f.Count("HostRotationPromotionEvidence")==0 && f.Count("HostRotationCurrentCredentialEvidence")==0 && f.Count("HostCapabilityGrants")==0);
        return Task.CompletedTask;
    }
    public static Task AtomicAuditAndFinalContextRollback()
    {
        foreach(var mode in Enumerable.Range(0,4))
        {
            using var f=new PeerTrustTests.Fixture();
            var action=mode switch {
                0=>"SELECT RAISE(ABORT,'fixture');",
                1=>"UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;",
                2=>"UPDATE HostIdentity SET CurrentCredentialRef=NULL;",
                _=>"UPDATE TrustedManagerPairings SET BoundUtc=BoundUtc;"
            };
            f.Execute("CREATE TRIGGER ChangeBinding AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PeerBoundCreated' BEGIN "+action+" END;");
            var refused=false;
            try {f.Repository.RecordOwnerVerifiedBinding(Owner(f),f.PeerId,Peer,Local);}
            catch(Exception ex) when(ex is SqliteException or AuthenticationException or InvalidDataException or InvalidOperationException) {refused=true;}
            Check(refused && f.Count("TrustedManagers")==0 && f.Count("PeerLocalBindingEvidence")==0 && f.Count("PeerRelationshipIncarnations")==0);
            Check(HostDatabase.QueryScalarLong(f.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerBoundCreated';")==0);
            f.Execute("DROP TRIGGER ChangeBinding;");
            Check(f.Repository.RecordOwnerVerifiedBinding(Owner(f),f.PeerId,Peer,Local).Disposition==PeerBindingDisposition.PeerBoundCreated);
            AssertActualBinding(f.Writer,f.PeerId,Local);
            Check(f.Count("HostCapabilityGrants")==0 && f.Count("ServerCapabilityGrants")==0);
        }
        return Task.CompletedTask;
    }
    public static async Task QueuedOwnerChangeAndFirstNewBinding()
    {
        using var f=new PeerTrustTests.Fixture();var owner=Owner(f);
        using(var tx=f.Writer.BeginTransaction())
        {
            HostDatabase.Execute(f.Writer,"UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;",tx);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var work=Task.Run(()=>{entered.SetResult();return f.Repository.RecordOwnerVerifiedBinding(owner,f.PeerId,Peer,Local);});
            try {await entered.Task;await Task.Delay(100);Check(!work.IsCompleted);} finally {tx.Commit();}
            try {await work.WaitAsync(TimeSpan.FromSeconds(10));throw new Exception("Queued stale Owner bound a peer.");}catch(AuthenticationException) { }
        }
        Check(f.Count("TrustedManagers")==0 && f.Count("PeerLocalBindingEvidence")==0);
        // Fixture current credential changed before the first binding; no rotation/promotion is fabricated.
        f.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{Next}' WHERE CredentialRef='current';");
        Reject<InvalidOperationException>(()=>f.Repository.RecordVerifiedBinding(f.PeerId,Peer,Local));
        Check(f.Repository.RecordOwnerVerifiedBinding(owner with {PublicVerificationKey="changed"},f.PeerId,Peer,Next).Disposition==PeerBindingDisposition.PeerBoundCreated);
        AssertActualBinding(f.Writer,f.PeerId,Next);
        Check(f.Count("HostCredentialRotations")==0 && f.Count("HostCredentialRotationPeers")==0 && f.Count("HostCapabilityGrants")==0);
    }
}
