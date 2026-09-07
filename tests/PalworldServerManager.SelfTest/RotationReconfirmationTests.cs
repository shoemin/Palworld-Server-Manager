using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static class RotationReconfirmationTests
{
    private static readonly string Local = new('A', 64), Old = new('B', 64), Next = new('C', 64), Later = new('D', 64);
    private static void Check(bool value) { if (!value) throw new Exception("Rotation reconfirmation assertion failed."); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected rotation reconfirmation refusal."); }
    private static LocalPrincipalMutationActor Owner(PeerTrustTests.Fixture f) => new(f.HostId, f.OwnerId, "native-owner", "fixture-public");
    private static PeerTrustRepository Stage(PeerTrustTests.Fixture f)
    {
        var repo = f.Repository; repo.RecordVerifiedBinding(f.PeerId, Old, Local);
        f.Execute($"""
            UPDATE TrustedManagers SET State='Active' WHERE PeerHostId='{f.PeerId:D}';
            INSERT INTO HostCapabilityGrants (GrantId,TargetHostId,Capability,GranteeActorKind,GranteePeerHostId,
                GrantedByActorKind,GrantedByLocalPrincipalId,CanDelegate,CanDelegateOnwardDelegation,CreatedUtc)
                VALUES ('custom','{f.HostId:D}','ViewHost','RemoteManager','{f.PeerId:D}','LocalPrincipal','{f.OwnerId:D}',1,0,'retained');
            """);
        repo.StagePeerRotation(new(f.PeerId, Guid.NewGuid(), 1, Old, Next), Old); return repo;
    }
    private static RoutineRotationStatusReply Reply(PeerTrustRepository.RotationStatusQuery query, RoutineRotationLiveState state) => new(query.Request, state);
    private static void Preserved(PeerTrustTests.Fixture f)
    {
        Check(f.Repository.Read(f.PeerId)!.State == "Active" && f.Count("HostCapabilityGrants") == 1 && f.Count("ServerCapabilityGrants") == 0);
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM HostCapabilityGrants WHERE GrantId='custom' AND CanDelegate=1 AND InvalidatedUtc IS NULL AND CreatedUtc='retained';") == 1);
    }
    public static Task RenewalRequiresLiveIntentAndFreshOwner()
    {
        using var f = new PeerTrustTests.Fixture(); var repo = Stage(f); var retained = repo.Read(f.PeerId)!;
        f.Time.Now += TimeSpan.FromMinutes(30);
        var passive = repo.BeginPeerRotationStatusQuery(f.PeerId);
        Check(repo.CompletePeerRotationStatusQuery(passive, Reply(passive, RoutineRotationLiveState.Staging), Old, Local) == PeerRotationResolution.Unchanged);
        Check(repo.Read(f.PeerId)!.PendingReconfirmationRequired && repo.Read(f.PeerId)!.PendingRotationExpiresUtc == retained.PendingRotationExpiresUtc);
        var staleOwner = repo.BeginPeerRotationStatusQuery(f.PeerId, Owner(f));
        f.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;");
        Reject<AuthenticationException>(() => repo.CompletePeerRotationStatusQuery(staleOwner, Reply(staleOwner, RoutineRotationLiveState.Staging), Old, Local));
        Reject<AuthenticationException>(() => repo.BeginPeerRotationStatusQuery(f.PeerId, Owner(f)));
        f.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='fixture-public' WHERE IsOwner=1;");
        var query = repo.BeginPeerRotationStatusQuery(f.PeerId, Owner(f));
        Check(repo.CompletePeerRotationStatusQuery(query, Reply(query, RoutineRotationLiveState.ReadyForCutover), Old, Local) == PeerRotationResolution.Renewed);
        var renewed = repo.Read(f.PeerId)!;
        Check(renewed.CurrentFingerprint == Old && renewed.PendingFingerprint == Next && renewed.PendingRotationId == retained.PendingRotationId &&
            !renewed.PendingReconfirmationRequired && renewed.PendingRotationExpiresUtc == f.Time.Now.AddMinutes(30));
        Reject<AuthenticationException>(() => repo.CompletePeerRotationStatusQuery(query, Reply(query, RoutineRotationLiveState.ReadyForCutover), Old, Local));
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerRotationReconfirmed' AND ActorKind='LocalPrincipal' AND ActorPeerHostId IS NULL;") == 1);
        Check(f.Count("TrustedManagerCredentialHistory") == 0); Preserved(f); return Task.CompletedTask;
    }
    public static Task AbandonmentAndUntrustedCutoverClaims()
    {
        foreach (var state in new[] { RoutineRotationLiveState.CutOver, RoutineRotationLiveState.Completed })
        {
            using var f = new PeerTrustTests.Fixture(); var repo = Stage(f); f.Time.Now += TimeSpan.FromHours(1);
            var query = repo.BeginPeerRotationStatusQuery(f.PeerId, Owner(f));
            Check(repo.CompletePeerRotationStatusQuery(query, Reply(query, state), Old, Local) == PeerRotationResolution.Unchanged);
            Check(repo.Read(f.PeerId)!.CurrentFingerprint == Old && repo.Read(f.PeerId)!.PendingFingerprint == Next && repo.Read(f.PeerId)!.PendingReconfirmationRequired);
            Check(f.Count("TrustedManagerCredentialHistory") == 0); Preserved(f);
        }
        foreach (var state in new[] { RoutineRotationLiveState.Aborted, RoutineRotationLiveState.Unknown })
        {
            using var f = new PeerTrustTests.Fixture(); var repo = Stage(f); f.Time.Now += TimeSpan.FromHours(1);
            var query = repo.BeginPeerRotationStatusQuery(f.PeerId);
            Check(repo.CompletePeerRotationStatusQuery(query, Reply(query, state), Old, Local) == PeerRotationResolution.Cleared);
            var row = repo.Read(f.PeerId)!; Check(row.CurrentFingerprint == Old && row.PendingFingerprint is null && row.PendingRotationId is null && row.PendingRotationExpiresUtc is null && !row.PendingReconfirmationRequired);
            Check(f.Count("PeerRotationProposals") == 1 && f.Count("TrustedManagerCredentialHistory") == 0); Preserved(f);
            Reject<AuthenticationException>(() => repo.BeginPeerRotationStatusQuery(f.PeerId));
        }
        return Task.CompletedTask;
    }
    private sealed class Clock(PeerTrustTests.Clock wall) : TimeProvider
    {
        internal long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
        public override DateTimeOffset GetUtcNow() => wall.Now;
    }
    public static Task QueryScopeReplayDeadlineAndChangedTuple()
    {
        using var f = new PeerTrustTests.Fixture(); Stage(f); var clock = new Clock(f.Time); var repo = new PeerTrustRepository(f.Database, f.HostId, clock);
        var query = repo.BeginPeerRotationStatusQuery(f.PeerId, Owner(f));
        Reject<AuthenticationException>(() => f.Repository.CompletePeerRotationStatusQuery(query, Reply(query, RoutineRotationLiveState.Unknown), Old, Local));
        clock.Seconds = 20; f.Time.Now -= TimeSpan.FromDays(1); // Wall rollback does not revive a monotonic-expired query.
        Reject<AuthenticationException>(() => repo.CompletePeerRotationStatusQuery(query, Reply(query, RoutineRotationLiveState.Unknown), Old, Local));
        query = repo.BeginPeerRotationStatusQuery(f.PeerId);
        Reject<AuthenticationException>(() => repo.CompletePeerRotationStatusQuery(query, new(query.Request with { QueryId = Guid.NewGuid() }, RoutineRotationLiveState.Unknown), Old, Local));
        query = repo.BeginPeerRotationStatusQuery(f.PeerId);
        Reject<AuthenticationException>(() => repo.CompletePeerRotationStatusQuery(query, new(query.Request with { NewFingerprint = Later }, RoutineRotationLiveState.Unknown), Old, Local));
        query = repo.BeginPeerRotationStatusQuery(f.PeerId);
        Reject<AuthenticationException>(() => repo.CompletePeerRotationStatusQuery(query, Reply(query, (RoutineRotationLiveState)99), Old, Local));
        query = repo.BeginPeerRotationStatusQuery(f.PeerId);
        f.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=1;");
        Reject<AuthenticationException>(() => repo.CompletePeerRotationStatusQuery(query, Reply(query, RoutineRotationLiveState.Unknown), Old, Local));
        Reject<AuthenticationException>(() => repo.BeginPeerRotationStatusQuery(f.PeerId, Owner(f)));
        f.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=0;");
        query = repo.BeginPeerRotationStatusQuery(f.PeerId);
        repo.StagePeerRotation(new(f.PeerId, Guid.NewGuid(), 2, Old, Later), Old);
        Reject<AuthenticationException>(() => repo.CompletePeerRotationStatusQuery(query, Reply(query, RoutineRotationLiveState.Unknown), Old, Local));
        Check(repo.Read(f.PeerId)!.PendingFingerprint == Later);
        query = repo.BeginPeerRotationStatusQuery(f.PeerId);
        repo.ObserveActivePeerCredential(f.PeerId, Later);
        Reject<AuthenticationException>(() => repo.CompletePeerRotationStatusQuery(query, Reply(query, RoutineRotationLiveState.Aborted), Old, Local));
        Check(repo.Read(f.PeerId)!.CurrentFingerprint == Later && repo.Read(f.PeerId)!.PendingRotationId is not null); Preserved(f); return Task.CompletedTask;
    }
    public static async Task ConcurrentRenewalAndAuditRollback()
    {
        using var f = new PeerTrustTests.Fixture(); var repo = Stage(f); f.Time.Now += TimeSpan.FromMinutes(30);
        var deadline = repo.Read(f.PeerId)!.PendingRotationExpiresUtc;
        foreach (var state in new[] { RoutineRotationLiveState.Staging, RoutineRotationLiveState.Aborted })
        {
            var query = repo.BeginPeerRotationStatusQuery(f.PeerId, Owner(f));
            f.Execute("CREATE TRIGGER FailResolution BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT,'fixture resolution audit failure'); END;");
            Reject<SqliteException>(() => repo.CompletePeerRotationStatusQuery(query, Reply(query, state), Old, Local));
            f.Execute("DROP TRIGGER FailResolution;");
            Check(repo.Read(f.PeerId)!.PendingRotationExpiresUtc == deadline && repo.Read(f.PeerId)!.PendingFingerprint == Next);
            Reject<AuthenticationException>(() => repo.CompletePeerRotationStatusQuery(query, Reply(query, state), Old, Local)); // New live query after ambiguous/failing completion.
        }
        var queries = Enumerable.Range(0, 12).Select(_ => repo.BeginPeerRotationStatusQuery(f.PeerId, Owner(f))).ToArray();
        var delayed = repo.BeginPeerRotationStatusQuery(f.PeerId, Owner(f));
        var results = await Task.WhenAll(queries.Select(query => Task.Run(() =>
        {
            try { return repo.CompletePeerRotationStatusQuery(query, Reply(query, RoutineRotationLiveState.Staging), Old, Local) == PeerRotationResolution.Renewed; }
            catch (AuthenticationException) { return false; }
        })));
        Check(results.Count(x => x) == 1);
        f.Time.Now += TimeSpan.FromMinutes(30);
        Reject<AuthenticationException>(() => repo.CompletePeerRotationStatusQuery(delayed, Reply(delayed, RoutineRotationLiveState.Staging), Old, Local));
        Reject<AuthenticationException>(() => repo.CompletePeerRotationStatusQuery(queries[0], Reply(queries[0], RoutineRotationLiveState.Staging), Old, Local));
        Preserved(f);
    }
    public static Task SenderStatusBindsTupleAndPresentedCurrent()
    {
        using var f = new PeerTrustTests.Fixture(); var repo = new HostCredentialStateRepository(f.Database, f.HostId); var owner = Owner(f);
        var rotation = repo.PrepareRoutineRotation(owner, Guid.NewGuid()); repo.RecordCreated(rotation.NewReference, Old);
        repo.BeginRoutineRotationStaging(owner, rotation.RotationId); var proposal = repo.PrepareRoutineRotationProposal(owner, rotation.RotationId);
        var request = new RoutineRotationStatusRequest(Guid.NewGuid(), f.HostId, rotation.RotationId, Local, Old);
        Check(repo.ReadRoutineRotationStatus(request, Local) == new RoutineRotationStatusReply(request, RoutineRotationLiveState.Staging));
        Reject<AuthenticationException>(() => repo.ReadRoutineRotationStatus(request, Old));
        Reject<AuthenticationException>(() => repo.ReadRoutineRotationStatus(request with { HostId = f.PeerId }, Local));
        Reject<AuthenticationException>(() => repo.ReadRoutineRotationStatus(request with { NewFingerprint = Next }, Local));
        Check(repo.ReadRoutineRotationStatus(request with { RotationId = Guid.NewGuid() }, Local).State == RoutineRotationLiveState.Unknown);
        repo.AbortRoutineRotation(owner, rotation.RotationId);
        Check(repo.ReadRoutineRotationStatus(request, Local).State == RoutineRotationLiveState.Aborted);
        f.Execute("DELETE FROM HostRotationProposals;"); // Legacy public tuple, no invented ordering on a status read.
        Check(repo.ReadRoutineRotationStatus(request, Local).State == RoutineRotationLiveState.Aborted && f.Count("HostRotationProposals") == 0);
        f.Execute($"UPDATE HostIdentity SET CurrentCredentialRef='{rotation.NewReference}'; UPDATE HostCredentialRotations SET State='CutOver' WHERE RotationId='{rotation.RotationId:D}';");
        Reject<AuthenticationException>(() => repo.ReadRoutineRotationStatus(request, Local));
        Check(repo.ReadRoutineRotationStatus(request, Old).State == RoutineRotationLiveState.CutOver);
        f.Execute("UPDATE HostCredentialRotations SET State='Completed';");
        Check(repo.ReadRoutineRotationStatus(request, Old).State == RoutineRotationLiveState.Completed);
        Check(f.Count("HostCapabilityGrants") == 0); return Task.CompletedTask;
    }
    public static Task DeadlineCrossingDuringAuditRollsBack()
    {
        using var f = new PeerTrustTests.Fixture(); Stage(f); f.Time.Now += TimeSpan.FromMinutes(30);
        // The deterministic clock advances when the final freshness check reads it. Both
        // mutation/audit must roll back when the exchange expires before commit.
        var crossing = new CrossingClock(f.Time); var repo = new PeerTrustRepository(f.Database, f.HostId, crossing);
        var deadline = repo.Read(f.PeerId)!.PendingRotationExpiresUtc;
        var query = repo.BeginPeerRotationStatusQuery(f.PeerId, Owner(f));
        Reject<AuthenticationException>(() => repo.CompletePeerRotationStatusQuery(query, Reply(query, RoutineRotationLiveState.Staging), Old, Local));
        Check(repo.Read(f.PeerId)!.PendingRotationExpiresUtc == deadline &&
            HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerRotationReconfirmed';") == 0);
        Preserved(f); return Task.CompletedTask;
    }
    private sealed class CrossingClock(PeerTrustTests.Clock wall) : TimeProvider
    {
        private int reads;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Interlocked.Increment(ref reads) < 3 ? 0 : 20;
        public override DateTimeOffset GetUtcNow() => wall.Now;
    }
}
