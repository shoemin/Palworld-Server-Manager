using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;

namespace PalworldServerManager.SelfTest;

internal static class RotationStagingTests
{
    private static readonly string Local = new('A', 64), Old = new('B', 64), Next = new('C', 64), Later = new('D', 64);
    private static void Check(bool value) { if (!value) throw new Exception("Rotation staging assertion failed."); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected rotation staging refusal."); }
    private static HostCredentialStateRepository State(PeerTrustTests.Fixture f) => new(f.Database, f.HostId);
    private static LocalPrincipalMutationActor Owner(PeerTrustTests.Fixture f) => new(f.HostId, f.OwnerId, "native-owner", "fixture-public");
    private static Guid Ready(PeerTrustTests.Fixture f, string fingerprint)
    {
        var prepared = State(f).PrepareRoutineRotation(Owner(f), Guid.NewGuid());
        State(f).RecordCreated(prepared.NewReference, fingerprint); State(f).BeginRoutineRotationStaging(Owner(f), prepared.RotationId); return prepared.RotationId;
    }
    private static void Active(PeerTrustTests.Fixture f)
    {
        f.Repository.RecordVerifiedBinding(f.PeerId, Old, Local);
        f.Execute($"""
            UPDATE TrustedManagers SET State='Active' WHERE PeerHostId='{f.PeerId:D}';
            INSERT INTO HostCapabilityGrants (GrantId,TargetHostId,Capability,GranteeActorKind,GranteePeerHostId,
                GrantedByActorKind,GrantedByLocalPrincipalId,CanDelegate,CanDelegateOnwardDelegation,CreatedUtc)
                VALUES ('custom','{f.HostId:D}','ViewHost','RemoteManager','{f.PeerId:D}','LocalPrincipal','{f.OwnerId:D}',1,0,'retained');
            """);
    }
    private static HostRotationProposal Proposal(PeerTrustTests.Fixture f, long sequence = 1) => new(f.PeerId, Guid.NewGuid(), sequence, Old, Next);
    private static void Preserved(PeerTrustTests.Fixture f)
    {
        Check(State(f).Read().CurrentReference == "current" && f.Repository.Read(f.PeerId)!.State == "Active");
        Check(f.Count("HostCapabilityGrants") == 1 && f.Count("ServerCapabilityGrants") == 0);
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM HostCapabilityGrants WHERE GrantId='custom' AND CanDelegate=1 AND InvalidatedUtc IS NULL AND CreatedUtc='retained';") == 1);
    }
    public static async Task SenderSequenceAndOwnerGate()
    {
        using var f = new PeerTrustTests.Fixture(); var rotation = Ready(f, Old); var owner = Owner(f);
        Reject<InvalidOperationException>(() => State(f).ReadRoutineRotationProposal(rotation));
        var proposals = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => State(f).PrepareRoutineRotationProposal(owner, rotation))));
        Check(proposals.Distinct().Count() == 1 && proposals[0].Sequence > 0 && proposals[0].HostId == f.HostId && proposals[0].OldFingerprint == Local && proposals[0].NewFingerprint == Old);
        Check(f.Count("HostRotationProposals") == 1 && State(f).ReadRoutineRotationProposal(rotation) == proposals[0]);
        f.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed-owner' WHERE IsOwner=1;");
        Reject<AuthenticationException>(() => State(f).PrepareRoutineRotationProposal(owner, rotation));
        Check(State(f).ReadRoutineRotationProposal(rotation) == proposals[0]); // Existing Host-owned retransmission, not a new local action.
        f.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='fixture-public' WHERE IsOwner=1;");
        State(f).AbortRoutineRotation(owner, rotation);
        Reject<AuthenticationException>(() => State(f).ReadRoutineRotationProposal(rotation));
        var next = Ready(f, Next);
        f.Execute("CREATE TRIGGER FailProposal BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='HostRoutineRotationProposalPrepared' BEGIN SELECT RAISE(ABORT,'fixture proposal audit failure'); END;");
        Reject<SqliteException>(() => State(f).PrepareRoutineRotationProposal(owner, next)); Check(f.Count("HostRotationProposals") == 1);
        f.Execute("DROP TRIGGER FailProposal;");
        var later = State(f).PrepareRoutineRotationProposal(owner, next); Check(later.Sequence > proposals[0].Sequence && later.RotationId != rotation);
        Check(State(f).Read().CurrentReference == "current" && f.Count("HostCapabilityGrants") == 0);
        f.Execute("UPDATE LocalPrincipals SET IsOwner=0; UPDATE HostIdentity SET HostBootstrapState='Uninitialized';");
        Reject<AuthenticationException>(() => State(f).ReadRoutineRotationProposal(next));
    }
    public static async Task ReceiverOrderingReplayAndRollback()
    {
        using var f = new PeerTrustTests.Fixture(); Active(f); var first = Proposal(f);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => f.Repository.StagePeerRotation(first, Old, Local))));
        Check(results.Count(r => r.Disposition == PeerRotationStagingDisposition.Staged) == 1 && f.Count("PeerRotationProposals") == 1);
        var deadline = f.Repository.Read(f.PeerId)!.PendingRotationExpiresUtc; Check(deadline == f.Time.Now.AddMinutes(30));
        Reject<AuthenticationException>(() => f.Repository.StagePeerRotation(Proposal(f, 2) with { OldFingerprint = Next, NewFingerprint = Later }, Next, Local));
        f.Time.Now += TimeSpan.FromMinutes(2);
        Check(new PeerTrustRepository(f.Database, f.HostId, f.Time).StagePeerRotation(first, Old, Local).ExpiresUtc == deadline);
        Reject<AuthenticationException>(() => f.Repository.StagePeerRotation(first with { NewFingerprint = Later }, Old, Local));
        Reject<AuthenticationException>(() => f.Repository.StagePeerRotation(first with { Sequence = 2 }, Old, Local));
        Reject<AuthenticationException>(() => f.Repository.StagePeerRotation(Proposal(f), Old, Local));
        var newer = Proposal(f, 2) with { NewFingerprint = Later };
        f.Execute("CREATE TRIGGER FailStage BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='PeerRotationStaged' BEGIN SELECT RAISE(ABORT,'fixture stage audit failure'); END;");
        Reject<SqliteException>(() => f.Repository.StagePeerRotation(newer, Old, Local)); Check(f.Count("PeerRotationProposals") == 1 && f.Repository.Read(f.PeerId)!.PendingRotationId == first.RotationId);
        f.Execute("DROP TRIGGER FailStage;");
        Check(f.Repository.StagePeerRotation(newer, Old, Local).Disposition == PeerRotationStagingDisposition.Staged);
        Reject<AuthenticationException>(() => f.Repository.StagePeerRotation(first, Old, Local));
        Check(f.Repository.Read(f.PeerId)!.PendingRotationId == newer.RotationId && f.Repository.Read(f.PeerId)!.CurrentFingerprint == Old);
        Check(f.Repository.RecognizesTransportFingerprint(Old) && f.Repository.RecognizesTransportFingerprint(Later)); Preserved(f);
    }
    public static Task LapseAndReceiptCannotBeOverwritten()
    {
        using var f = new PeerTrustTests.Fixture(); Active(f); var first = Proposal(f); f.Repository.StagePeerRotation(first, Old, Local);
        f.Time.Now += TimeSpan.FromMinutes(30);
        var newer = Proposal(f, 2) with { NewFingerprint = Later };
        f.Execute("CREATE TRIGGER FailLapse BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='PeerRotationReconfirmationRequired' BEGIN SELECT RAISE(ABORT,'fixture lapse audit failure'); END;");
        Reject<SqliteException>(() => f.Repository.StagePeerRotation(newer, Old, Local));
        Check(!f.Repository.Read(f.PeerId)!.PendingReconfirmationRequired && f.Count("PeerRotationProposals") == 1);
        f.Execute("DROP TRIGGER FailLapse;");
        var blocked = f.Repository.StagePeerRotation(newer, Old, Local);
        Check(blocked.Disposition == PeerRotationStagingDisposition.ReconfirmationRequired && blocked.RetainedRotationId == first.RotationId);
        Check(f.Repository.Read(f.PeerId)!.PendingReconfirmationRequired && f.Count("PeerRotationProposals") == 1);
        Check(f.Repository.StagePeerRotation(first, Old, Local).Disposition == PeerRotationStagingDisposition.ReconfirmationRequired);
        f.Repository.ObserveActivePeerCredential(f.PeerId, Next);
        var afterPromotion = newer with { OldFingerprint = Next, Sequence = 1 };
        blocked = f.Repository.StagePeerRotation(afterPromotion, Next, Local);
        Check(blocked.Disposition == PeerRotationStagingDisposition.PromotionReceiptPending && blocked.RetainedRotationId == first.RotationId && f.Count("PeerRotationProposals") == 1);
        f.Repository.ConfirmPeerRotationReceipt(f.PeerId, Next, first.RotationId);
        Check(f.Repository.StagePeerRotation(afterPromotion, Next, Local).Disposition == PeerRotationStagingDisposition.Staged); // New current-key epoch.
        Reject<AuthenticationException>(() => f.Repository.StagePeerRotation(first, Old, Local));
        Check(f.Repository.Read(f.PeerId)!.CurrentFingerprint == Next); Preserved(f); return Task.CompletedTask;
    }
    public static Task ClosedStatesAndIdentity()
    {
        using var f = new PeerTrustTests.Fixture(); var proposal = Proposal(f);
        f.Repository.RecordVerifiedBinding(f.PeerId, Old, Local);
        Reject<AuthenticationException>(() => f.Repository.StagePeerRotation(proposal, Old, Local));
        f.Execute($"UPDATE TrustedManagers SET State='Active' WHERE PeerHostId='{f.PeerId:D}';");
        foreach (var invalid in new[] { proposal with { Sequence = 0 }, proposal with { Sequence = -1 }, proposal with { HostId = f.HostId }, proposal with { HostId = Guid.NewGuid() }, proposal with { NewFingerprint = Old } })
            Reject<AuthenticationException>(() => f.Repository.StagePeerRotation(invalid, Old, Local));
        Reject<AuthenticationException>(() => f.Repository.StagePeerRotation(proposal, Next, Local));
        f.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{f.PeerId:D}';");
        Reject<AuthenticationException>(() => f.Repository.StagePeerRotation(proposal, Old, Local));
        Check(f.Count("PeerRotationProposals") == 0 && f.Repository.Read(f.PeerId)!.PendingFingerprint is null); return Task.CompletedTask;
    }
    public static Task UpgradeDoesNotInventOrdering()
    {
        using var f = new PeerTrustTests.Fixture(schemaVersion: 3); Active(f); var retained = Guid.NewGuid(); var deadline = f.Time.Now.AddMinutes(10);
        f.Execute($"UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint='{Next}',PendingRotationId='{retained:D}',PendingRotationExpiresUtc='{deadline:O}' WHERE PeerHostId='{f.PeerId:D}';");
        Check(HostSchemaMigrationRunner.Default().Migrate(f.Writer) == 1 && HostSchemaMigrationRunner.Default().Migrate(f.Writer) == 0);
        Check(f.Count("PeerRotationProposals") == 0 && f.Count("HostRotationProposals") == 0 && f.Repository.Read(f.PeerId)!.PendingRotationExpiresUtc == deadline);
        Reject<InvalidDataException>(() => f.Repository.StagePeerRotation(Proposal(f), Old, Local));
        Check(f.Repository.ObserveActivePeerCredential(f.PeerId, Next).Promoted); // Existing verified live pin remains valid.
        Check(f.Repository.ConfirmPeerRotationReceipt(f.PeerId, Next, retained));
        Check(f.Repository.StagePeerRotation(Proposal(f) with { OldFingerprint = Next, NewFingerprint = Later }, Next, Local).Disposition == PeerRotationStagingDisposition.Staged);
        Preserved(f); return Task.CompletedTask;
    }
}
