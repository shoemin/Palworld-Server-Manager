using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;

namespace PalworldServerManager.SelfTest;

internal static partial class RotationCompletionTests
{
    public static async Task DeletionFailureAndCompletionAuditRecover()
    {
        foreach(var mode in Enumerable.Range(0,4))
        {
            using var r=new Rig();r.Receipt(r.A);r.Confirm(r.B);r.Complete();
            var native=true;var secret=true;var fail=true;
            HostTrustReconciler Reopened()=>new(()=>r.State.Read(),(_,_)=>
            {
                if(fail && mode==0)throw new IOException("fixture publication");return Task.CompletedTask;
            },(retained,_)=>
            {
                Check(!retained.Contains(r.Rotation.OldReference));
                if(fail && mode==1)throw new IOException("fixture native deletion");
                native=false;return Task.CompletedTask;
            },(reference,_)=>
            {
                Check(reference==r.Rotation.OldReference && !native);
                if(fail && mode==2)throw new IOException("fixture protected deletion");
                secret=false;return Task.CompletedTask;
            },reference=>
            {
                Check(!native && !secret);r.State.RecordRetired(reference);
            });
            if(mode==3)r.F.Execute("CREATE TRIGGER final_retirement_failure BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='HostRoutineRotationCompleted' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
            var refused=false;try{await Reopened().ReconcileAsync();}catch(Exception ex)when(ex is IOException or SqliteException){refused=true;}
            Check(refused && r.State.Read().Rotations.Single() is {State:HostCredentialRotationState.CutOver,RetirementAuthorized:true});
            Check(!r.State.Read().Credentials.Single(c=>c.Reference==r.Rotation.OldReference).Retired);
            Check(HostDatabase.QueryScalarLong(r.F.Writer,"SELECT COUNT(*) FROM HostCredentialRotations WHERE CompletedUtc IS NOT NULL;")==0);
            Check(HostDatabase.QueryScalarLong(r.F.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRoutineRotationCompleted';")==0);
            Check(native==(mode<=1) && secret==(mode<=2));
            if(mode==3)r.F.Execute("DROP TRIGGER final_retirement_failure;");
            fail=false;await Reopened().ReconcileAsync();await Reopened().ReconcileAsync();
            Check(!native && !secret && r.State.Read().Rotations.Single().State==HostCredentialRotationState.Completed);
            Check(r.State.Read().Credentials.Single(c=>c.Reference==r.Rotation.OldReference).Retired);
            Check(HostDatabase.QueryScalarLong(r.F.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRoutineRotationRetirementAuthorized' AND ActorKind='LocalPrincipal';")==1);
            Check(HostDatabase.QueryScalarLong(r.F.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRoutineRotationCompleted' AND ActorKind IS NULL;")==1);
        }
    }
    public static Task UpgradePreservesIntentWithoutInventingDeletion()
    {
        foreach(var retired in new[]{false,true})
        {
            using var f=new PeerTrustTests.Fixture(schemaVersion:8);var rotation=Guid.NewGuid();
            f.Execute($"""
                INSERT INTO SecureCredentialReferences (CredentialRef,Purpose,CreatedUtc,PublicKeyFingerprint,ActivatedUtc)
                    VALUES ('new','HostTlsV1','fixture','{New}','fixture');
                UPDATE HostIdentity SET CurrentCredentialRef='new';
                INSERT INTO HostCredentialRotations (RotationId,OldCredentialRef,NewCredentialRef,State,StartedUtc,CutOverUtc,CompletedUtc)
                    VALUES ('{rotation:D}','current','new','Completed','fixture','fixture','original-completed');
                """);
            if(retired)f.Execute("UPDATE SecureCredentialReferences SET RetiredUtc='original-retired' WHERE CredentialRef='current';");
            new HostSchemaMigrationRunner(HostSchema.AllMigrations()).Migrate(f.Writer);
            var state=new HostCredentialStateRepository(f.Database,f.HostId);var row=state.Read().Rotations.Single();
            Check(row.State==(retired?HostCredentialRotationState.Completed:HostCredentialRotationState.CutOver) && row.RetirementAuthorized==!retired);
            Check(state.Read().Credentials.Single(c=>c.Reference=="current").Retired==retired);
            Check(HostDatabase.QueryScalarText(f.Writer,"SELECT CompletedUtc FROM HostCredentialRotations;")== (retired?"original-completed":""));
            Check(HostTrustPlanning.Build(state.Read()).Retire.Contains("current"));
            Check(f.Count("HostCapabilityGrants")==0 && f.Count("ServerCapabilityGrants")==0);
            Check(HostDatabase.QueryScalarLong(f.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRoutineRotationCompleted';")==0);
            // Schema upgrade did not fabricate a deletion/audit. A later post-deletion callback resolves it.
            state.RecordRetired("current");Check(state.Read().Rotations.Single().State==HostCredentialRotationState.Completed);
        }
        return Task.CompletedTask;
    }
    public static Task RecoverySupersedesIntentAndFinalAuditRollsBack()
    {
        using(var r=new Rig())
        {
            r.Receipt(r.A);r.Confirm(r.B);r.Complete();
            r.State.PlanCredential("recovery");r.State.RecordCreated("recovery",new string('D',64));
            r.State.ReplaceOffline("recovery",MachineCredentialRecoveryReason.SuspectedCompromise);
            Check(r.State.Read().Rotations.Single() is {State:HostCredentialRotationState.Aborted,RetirementAuthorized:false});
            r.State.RecordRetired(r.Rotation.OldReference);
            Check(r.State.Read().Rotations.Single().State==HostCredentialRotationState.Aborted);
            Check(HostDatabase.QueryScalarLong(r.F.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRoutineRotationCompleted';")==0);
        }
        using(var r=new Rig())
        {
            r.Receipt(r.A);r.Confirm(r.B);r.Complete();
            r.F.Execute("CREATE TRIGGER change_retirement AFTER INSERT ON AuditEvents WHEN NEW.EventKind='HostRoutineRotationCompleted' BEGIN UPDATE HostCredentialRotations SET RetirementAuthorized=0; END;");
            Reject<System.Security.Authentication.AuthenticationException>(()=>r.State.RecordRetired(r.Rotation.OldReference));
            Check(r.State.Read().Rotations.Single() is {State:HostCredentialRotationState.CutOver,RetirementAuthorized:true});
            Check(!r.State.Read().Credentials.Single(c=>c.Reference==r.Rotation.OldReference).Retired);
            r.F.Execute("DROP TRIGGER change_retirement;");r.State.RecordRetired(r.Rotation.OldReference);
            Check(r.State.Read().Rotations.Single().State==HostCredentialRotationState.Completed);
        }
        return Task.CompletedTask;
    }
}
