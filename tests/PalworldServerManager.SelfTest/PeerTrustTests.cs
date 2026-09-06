using Microsoft.Data.Sqlite;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.SelfTest;

internal static class PeerTrustTests
{
    private static readonly string Local = new('A', 64), Peer = new('B', 64), Changed = new('C', 64);
    private static void Check(bool value) { if (!value) throw new Exception("Peer trust assertion failed."); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected peer trust refusal."); }
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "PSMPeerTrust" + Guid.NewGuid().ToString("N"));
        internal readonly Guid HostId = Guid.NewGuid(), PeerId = Guid.NewGuid(), OwnerId = Guid.NewGuid();
        internal readonly HostDatabase Database;
        internal readonly SqliteConnection Writer;
        internal readonly Clock Time = new();
        private readonly HostExclusivityLock lease;
        internal PeerTrustRepository Repository => new(Database, HostId, Time);
        internal Fixture(bool priorVersion = false)
        {
            lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero, @"Global\PSMPeer" + Guid.NewGuid().ToString("N"))!;
            Database = new(new HostDataRoot(root)); Writer = Database.OpenConnection();
            new HostSchemaMigrationRunner(HostSchema.AllMigrations().Where(m => !priorVersion || m.Version <= 2)).Migrate(Writer);
            var identity = new HostIdentityRepository(Database); identity.EnsureHostIdentity(Writer, hostIdFactory: () => HostId.ToString("D"));
            using var tx = Writer.BeginTransaction(); identity.InitializeWithOwner(Writer, tx, OwnerId.ToString("D"), "native-owner", "fixture-public");
            using var command = Writer.CreateCommand(); command.Transaction = tx;
            command.CommandText = """
                INSERT INTO SecureCredentialReferences (CredentialRef,Purpose,CreatedUtc,PublicKeyFingerprint,ActivatedUtc)
                    VALUES ('current','HostTlsV1',$now,$fp,$now);
                UPDATE HostIdentity SET CurrentCredentialRef='current' WHERE Id=1;
                """;
            command.Parameters.AddWithValue("$now", Time.Now.ToString("O")); command.Parameters.AddWithValue("$fp", Local);
            command.ExecuteNonQuery(); tx.Commit();
        }
        internal void Execute(string sql) { using var c = Writer.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        internal long Count(string table) => HostDatabase.QueryScalarLong(Writer, "SELECT COUNT(*) FROM " + table + ";");
        public void Dispose() { Writer.Dispose(); SqliteConnection.ClearAllPools(); lease.Dispose(); Directory.Delete(root, true); }
    }
    public static Task DurableAndIdempotent()
    {
        using var f = new Fixture(); var repo = f.Repository;
        var created = repo.RecordVerifiedBinding(f.PeerId, Peer, Local);
        Check(created.Disposition == PeerBindingDisposition.PeerBoundCreated);
        Check(f.Count("HostCapabilityGrants") == 0 && f.Count("ServerCapabilityGrants") == 0);
        var initial = f.Repository.Read(f.PeerId)!; Check(initial.State == "PeerBound" && initial.LocalBoundFingerprint == Local);
        f.Time.Now += TimeSpan.FromMinutes(5);
        var outcomes = new PeerBindingResult[8];
        Parallel.For(0, 8, i => outcomes[i] = f.Repository.RecordVerifiedBinding(f.PeerId, Peer, Local));
        Check(outcomes.All(r => r.Disposition == PeerBindingDisposition.ResumePeerBound && r.ExpiresUtc == created.ExpiresUtc));
        Check(f.Count("TrustedManagers") == 1 && f.Count("TrustedManagerPairings") == 1 && f.Count("PendingCredentialReplacements") == 0);
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerBoundCreated';") == 1);
        // New repository/connection recovers durable public state without any ephemeral code.
        Check(f.Repository.Read(f.PeerId) == initial);
        var candidate = repo.RecordVerifiedBinding(f.PeerId, Changed, Local);
        Check(candidate.Disposition == PeerBindingDisposition.ReplacementRequired && candidate.ReplacementId is not null);
        Check(repo.RecordVerifiedBinding(f.PeerId, Changed, Local) == candidate);
        Check(repo.Read(f.PeerId) == initial && f.Count("PendingCredentialReplacements") == 1);
        return Task.CompletedTask;
    }
    public static Task ExpiryAndExistingIdentity()
    {
        using var f = new Fixture(); var repo = f.Repository;
        repo.RecordVerifiedBinding(f.PeerId, Peer, Local); var pending = repo.RecordVerifiedBinding(f.PeerId, Changed, Local);
        f.Execute($"UPDATE PendingCredentialReplacements SET ApprovedUtc='{f.Time.Now:O}',ApprovedByOwnerLocalPrincipalId='{f.OwnerId:D}' WHERE ReplacementId='{pending.ReplacementId:D}';");
        f.Time.Now += TimeSpan.FromMinutes(30);
        Check(repo.ExpirePending() == 1 && repo.ExpirePending() == 0);
        var revoked = repo.Read(f.PeerId)!; Check(revoked.State == "Revoked" && revoked.CurrentFingerprint is null);
        Check(f.Count("TrustedManagers") == 1 && f.Count("HostCapabilityGrants") == 0);
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM PendingCredentialReplacements WHERE InvalidatedUtc IS NOT NULL;") == 1);
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerBoundExpired';") == 1);
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerBoundExpired' AND ActorKind IS NULL AND ActorPeerHostId IS NULL;") == 1);
        var expiryAudit = HostDatabase.QueryScalarText(f.Writer, "SELECT Summary FROM AuditEvents WHERE EventKind='PeerBoundExpired';")!;
        Check(expiryAudit.Contains(f.PeerId.ToString("D")) && !expiryAudit.Contains(Peer) && !expiryAudit.Contains(Local));
        Check(repo.RecordVerifiedBinding(f.PeerId, Peer, Local).Disposition == PeerBindingDisposition.ReplacementRequired);
        Check(repo.Read(f.PeerId)!.State == "Revoked");
        // Fixture establishes existing Active trust; this repository has no activation operation.
        f.Execute($"UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='{Peer}',RevokedUtc=NULL WHERE PeerHostId='{f.PeerId:D}';");
        // An existing authority fixture must survive reconfirmation without rewrite or reissuance.
        f.Execute($"""
            INSERT INTO HostCapabilityGrants (GrantId,TargetHostId,Capability,GranteeActorKind,GranteePeerHostId,
                GrantedByActorKind,GrantedByLocalPrincipalId,CanDelegate,CanDelegateOnwardDelegation,CreatedUtc)
            VALUES ('retained','{f.HostId:D}','ViewHost','RemoteManager','{f.PeerId:D}','LocalPrincipal','{f.OwnerId:D}',0,0,'retained');
            """);
        var before = f.Count("AuditEvents");
        Check(repo.RecordVerifiedBinding(f.PeerId, Peer, Local).Disposition == PeerBindingDisposition.ActiveReconfirmed);
        Check(f.Count("AuditEvents") == before && f.Count("HostCapabilityGrants") == 1);
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM HostCapabilityGrants WHERE GrantId='retained' AND CreatedUtc='retained' AND InvalidatedUtc IS NULL;") == 1);
        f.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{f.PeerId:D}';");
        Check(repo.RecordVerifiedBinding(f.PeerId, Peer, Local).Disposition == PeerBindingDisposition.RecoveryRequired);
        Check(repo.Read(f.PeerId)!.RecoveryRequired);
        return Task.CompletedTask;
    }
    public static Task RollbackAndCredentialRaces()
    {
        using var f = new Fixture(); var repo = f.Repository;
        f.Execute("CREATE TRIGGER AuditFailure BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT,'fixture failure'); END;");
        Reject<SqliteException>(() => repo.RecordVerifiedBinding(f.PeerId, Peer, Local));
        Check(f.Count("TrustedManagers") == 0 && f.Count("TrustedManagerPairings") == 0);
        f.Execute("DROP TRIGGER AuditFailure;");
        Reject<ArgumentException>(() => repo.RecordVerifiedBinding(f.HostId, Peer, Local));
        Reject<ArgumentException>(() => repo.RecordVerifiedBinding(f.PeerId, "bad", Local));
        Reject<InvalidOperationException>(() => repo.RecordVerifiedBinding(f.PeerId, Peer, Changed));
        Check(f.Count("TrustedManagers") == 0);
        var outcomes = new PeerBindingResult[2];
        Parallel.Invoke(() => outcomes[0] = f.Repository.RecordVerifiedBinding(f.PeerId, Peer, Local),
            () => outcomes[1] = f.Repository.RecordVerifiedBinding(f.PeerId, Changed, Local));
        Check(outcomes.Count(r => r.Disposition == PeerBindingDisposition.PeerBoundCreated) == 1);
        Check(outcomes.Count(r => r.Disposition == PeerBindingDisposition.ReplacementRequired) == 1);
        var retained = repo.Read(f.PeerId)!;
        f.Time.Now += TimeSpan.FromMinutes(30);
        f.Execute("CREATE TRIGGER ExpiryAuditFailure BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT,'fixture failure'); END;");
        Reject<SqliteException>(() => repo.ExpirePending()); Check(repo.Read(f.PeerId) == retained);
        f.Execute("DROP TRIGGER ExpiryAuditFailure;"); Check(repo.ExpirePending() == 1);
        return Task.CompletedTask;
    }
    public static Task PriorSchemaDoesNotInventProof()
    {
        using var f = new Fixture(priorVersion: true);
        f.Execute($"INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('{f.PeerId:D}','PeerBound','{Peer}','{f.Time.Now:O}');");
        Check(HostSchemaMigrationRunner.Default().Migrate(f.Writer) == 1);
        var row = f.Repository.Read(f.PeerId)!;
        Check(row.LocalBoundFingerprint is null && row.ExpiresUtc == f.Time.Now.AddMinutes(30));
        Reject<InvalidDataException>(() => f.Repository.RecordVerifiedBinding(f.PeerId, Peer, Local));
        f.Time.Now += TimeSpan.FromMinutes(30); Check(f.Repository.ExpirePending() == 1);
        Check(f.Repository.Read(f.PeerId)!.State == "Revoked");
        return Task.CompletedTask;
    }
    public static Task OfflineRecoveryBlocksPendingTrust()
    {
        using var f = new Fixture(); var repo = f.Repository;
        repo.RecordVerifiedBinding(f.PeerId, Peer, Local); repo.RecordVerifiedBinding(f.PeerId, Changed, Local);
        var recovered = new string('D', 64);
        f.Execute($"INSERT INTO SecureCredentialReferences (CredentialRef,Purpose,CreatedUtc,PublicKeyFingerprint) VALUES ('recovered','HostTlsV1','{f.Time.Now:O}','{recovered}');");
        new HostCredentialStateRepository(f.Database, f.HostId).ReplaceOffline("recovered", MachineCredentialRecoveryReason.CredentialLoss);
        Check(repo.Read(f.PeerId)!.RecoveryRequired);
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM PendingCredentialReplacements WHERE InvalidatedUtc IS NOT NULL;") == 1);
        Check(repo.RecordVerifiedBinding(f.PeerId, Peer, recovered).Disposition == PeerBindingDisposition.RecoveryRequired);
        Check(repo.Read(f.PeerId)!.State == "PeerBound" && f.Count("HostCapabilityGrants") == 0);
        return Task.CompletedTask;
    }
}
