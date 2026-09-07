using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static class RoutineRotationPreparationTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Routine rotation preparation assertion failed."); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected routine rotation refusal."); }
    private static LocalPrincipalMutationActor Owner(PeerTrustTests.Fixture f) => new(f.HostId, f.OwnerId, "native-owner", "fixture-public");
    private static HostCredentialStateRepository Repo(PeerTrustTests.Fixture f) => new(f.Database, f.HostId);
    public static Task SerializedAndResumable()
    {
        using var f = new PeerTrustTests.Fixture(); var results = new RoutineRotationPreparation[12]; var owner = Owner(f);
        Parallel.For(0, results.Length, i => results[i] = Repo(f).PrepareRoutineRotation(owner, Guid.NewGuid()));
        var first = results[0]; Check(results.All(r => r == first));
        Check(f.Count("HostCredentialRotations") == 1 && f.Count("SecureCredentialReferences") == 2);
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRoutineRotationPrepared';") == 1);
        Check(!first.PublicMetadataReady && first.State == HostCredentialRotationState.Prepared && first.OldReference == "current");
        var plan = HostTrustPlanning.Build(Repo(f).Read());
        Check(plan.Publication!.CurrentFingerprint == new string('A', 64) && plan.Publication.PendingFingerprint is null && plan.Retained.Contains(first.NewReference));
        Reject<InvalidOperationException>(() => Repo(f).RecordRetired(first.NewReference));
        Check(Repo(f).PrepareRoutineRotation(owner, first.RotationId) == first);
        Reject<InvalidDataException>(() => Repo(f).BeginRoutineRotationStaging(owner, first.RotationId));
        Repo(f).RecordCreated(first.NewReference, new('B', 64));
        var staged = Repo(f).BeginRoutineRotationStaging(owner, first.RotationId);
        Check(staged.State == HostCredentialRotationState.Staging && staged.PublicMetadataReady);
        Check(Repo(f).BeginRoutineRotationStaging(owner, first.RotationId) == staged);
        plan = HostTrustPlanning.Build(Repo(f).Read());
        Check(plan.Publication!.CurrentFingerprint == new string('A', 64) && plan.Publication.PendingFingerprint == new string('B', 64) && plan.Publication.PendingRotationId == first.RotationId);
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRoutineRotationStaging';") == 1);
        Check(f.Count("HostCapabilityGrants") == 0 && f.Count("ServerCapabilityGrants") == 0 && f.Count("TrustedManagers") == 0);
        return Task.CompletedTask;
    }
    public static Task OwnerFreshnessAndRollback()
    {
        using var f = new PeerTrustTests.Fixture(); var owner = Owner(f); var request = Guid.NewGuid();
        Reject<AuthenticationException>(() => Repo(f).PrepareRoutineRotation(owner with { HostId = Guid.NewGuid() }, request));
        Reject<AuthenticationException>(() => Repo(f).PrepareRoutineRotation(owner with { LocalPrincipalId = Guid.NewGuid() }, request));
        Reject<AuthenticationException>(() => Repo(f).PrepareRoutineRotation(owner with { OsPrincipalRef = "other-native" }, request));
        Reject<AuthenticationException>(() => Repo(f).PrepareRoutineRotation(owner with { PublicVerificationKey = "stale-key" }, request));
        f.Execute("CREATE TRIGGER FailRotationAudit BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT,'fixture audit failure'); END;");
        Reject<SqliteException>(() => Repo(f).PrepareRoutineRotation(owner, request));
        Check(f.Count("HostCredentialRotations") == 0 && f.Count("SecureCredentialReferences") == 1);
        f.Execute("DROP TRIGGER FailRotationAudit;"); var prepared = Repo(f).PrepareRoutineRotation(owner, request);
        Repo(f).RecordCreated(prepared.NewReference, new('B', 64));
        f.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='replacement-owner-key' WHERE IsOwner=1;");
        Reject<AuthenticationException>(() => Repo(f).BeginRoutineRotationStaging(owner, request));
        Reject<AuthenticationException>(() => Repo(f).AbortRoutineRotation(owner, request));
        var refreshed = owner with { PublicVerificationKey = "replacement-owner-key" };
        f.Execute("CREATE TRIGGER FailRotationAudit BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT,'fixture audit failure'); END;");
        Reject<SqliteException>(() => Repo(f).BeginRoutineRotationStaging(refreshed, request));
        Check(Repo(f).Read().Rotations.Single().State == HostCredentialRotationState.Prepared);
        Reject<SqliteException>(() => Repo(f).AbortRoutineRotation(refreshed, request));
        Check(Repo(f).Read().Rotations.Single().State == HostCredentialRotationState.Prepared);
        f.Execute("DROP TRIGGER FailRotationAudit;"); Repo(f).AbortRoutineRotation(refreshed, request);
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE ActorKind='LocalPrincipal' AND ActorLocalPrincipalId IS NOT NULL AND IsOfflineRecovery=0;") == 2);
        return Task.CompletedTask;
    }
    public static async Task AbortRetentionAndCutoverGate()
    {
        using var f = new PeerTrustTests.Fixture(); var owner = Owner(f); var request = Guid.NewGuid(); var prepared = Repo(f).PrepareRoutineRotation(owner, request);
        Repo(f).RecordCreated(prepared.NewReference, new('A', 64)); // Same key cannot become a rotation, but reservation can still be aborted.
        Reject<AuthenticationException>(() => Repo(f).BeginRoutineRotationStaging(owner, request));
        var aborted = Repo(f).AbortRoutineRotation(owner, request); Check(aborted.State == HostCredentialRotationState.Aborted);
        Check(Repo(f).AbortRoutineRotation(owner, request) == aborted && Repo(f).PrepareRoutineRotation(owner, request) == aborted);
        var deleted = new List<string>();
        await new HostTrustReconciler(Repo(f).Read,
            (p, _) => { Check(p.CurrentFingerprint == new string('A', 64) && p.PendingFingerprint is null); return Task.CompletedTask; },
            (retained, _) => { Check(retained.SequenceEqual(new[] { "current" })); return Task.CompletedTask; },
            (reference, _) => { deleted.Add(reference); return Task.CompletedTask; }, Repo(f).RecordRetired).ReconcileAsync();
        Check(deleted.SequenceEqual(new[] { prepared.NewReference }));
        var next = Repo(f).PrepareRoutineRotation(owner, Guid.NewGuid()); Check(next.RotationId != request && next.NewReference != prepared.NewReference);
        Repo(f).RecordCreated(next.NewReference, new('C', 64)); Repo(f).BeginRoutineRotationStaging(owner, next.RotationId);
        // A later-engine CutOver fixture: this unit has no method that performs cutover.
        f.Execute($"UPDATE HostCredentialRotations SET State='CutOver' WHERE RotationId='{next.RotationId:D}'; UPDATE HostIdentity SET CurrentCredentialRef='{next.NewReference}';");
        Check(Repo(f).PrepareRoutineRotation(owner, Guid.NewGuid()).RotationId == next.RotationId);
        Reject<AuthenticationException>(() => Repo(f).AbortRoutineRotation(owner, next.RotationId));
        var plan = HostTrustPlanning.Build(Repo(f).Read()); Check(plan.Retained.Contains("current") && plan.Retained.Contains(next.NewReference));
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRoutineRotationAborted';") == 1);
        Check(f.Count("HostCapabilityGrants") == 0 && f.Count("ServerCapabilityGrants") == 0);
    }
}
