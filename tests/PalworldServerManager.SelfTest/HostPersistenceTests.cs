using Microsoft.Data.Sqlite;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;

namespace PalworldServerManager.SelfTest;

// #40 persistence foundation tests.
//
// ISOLATION RULE: every test here runs against a freshly created temporary directory and
// database, disposed afterward. No test ever touches a developer's real machine-wide Manager
// data root - a Host data path is machine-global by nature, so this is enforced by construction
// (HostDataRoot is always constructed from TempRoot()) rather than by convention.
public static class HostPersistenceTests
{
    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Directory = Path.Combine(Path.GetTempPath(), "psm-selftest-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Root = new HostDataRoot(Directory);
            Database = new HostDatabase(Root);
        }

        public string Directory { get; }

        public HostDataRoot Root { get; }

        public HostDatabase Database { get; }

        public SqliteConnection OpenMigrated()
        {
            var connection = Database.OpenConnection();
            HostSchemaMigrationRunner.Default().Migrate(connection);
            return connection;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { System.IO.Directory.Delete(Directory, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"{what}: expected {expected}, got {actual}");
        }
    }

    private static void True(bool condition, string what)
    {
        if (!condition)
        {
            throw new Exception($"Expected condition to hold: {what}");
        }
    }

    private static void Throws<TException>(Action action, string what) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new Exception($"{what}: expected {typeof(TException).Name} but got {ex.GetType().Name}: {ex.Message}");
        }

        throw new Exception($"{what}: expected {typeof(TException).Name} but no exception was thrown");
    }

    private static void Execute(SqliteConnection connection, string sql) => HostDatabase.Execute(connection, sql);

    // ---------------------------------------------------------------- connection foundation

    public static Task TestWalJournalModeEnabled()
    {
        using var temp = new TempRoot();
        using var connection = temp.Database.OpenConnection();
        Equal("wal", HostDatabase.QueryScalarText(connection, "PRAGMA journal_mode;").ToLowerInvariant(), "journal_mode");
        return Task.CompletedTask;
    }

    public static Task TestForeignKeysEnforcedOnManagedConnections()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        Equal(1L, HostDatabase.QueryScalarLong(connection, "PRAGMA foreign_keys;"), "foreign_keys pragma");

        // Prove it actually enforces, not merely that the pragma reads back as on.
        Throws<SqliteException>(
            () => Execute(connection, """
                INSERT INTO PendingCredentialReplacements
                    (ReplacementId, PeerHostId, ProposedKeyFingerprint, VerifiedUtc, ExpectedTrustState, ExpectedCurrentTrustedPublicKeyFingerprint, ExpiresUtc, CreatedUtc)
                VALUES ('r1', 'no-such-peer', 'fp', '2026-01-01T00:00:00Z', 'Active', 'fp-old', '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                """),
            "FK violation must be rejected");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- migrations

    public static Task TestFreshDatabaseMigratesToLatest()
    {
        using var temp = new TempRoot();
        using var connection = temp.Database.OpenConnection();
        var runner = HostSchemaMigrationRunner.Default();

        Equal(0, HostSchemaMigrationRunner.ReadSchemaVersion(connection), "fresh user_version");
        var applied = runner.Migrate(connection);
        True(applied > 0, "at least one migration applied to a fresh database");
        Equal(runner.LatestVersion, HostSchemaMigrationRunner.ReadSchemaVersion(connection), "user_version after migrate");

        // Sanity: the schema really exists.
        Equal(1L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='HostIdentity';"), "HostIdentity table present");
        return Task.CompletedTask;
    }

    // A TEST-ONLY migration chain. Deliberately not a production Migration002 - inventing a
    // fake production migration merely to satisfy a test would be worse than the weak test it
    // replaces.
    private sealed class SyntheticMigration : IHostSchemaMigration
    {
        private readonly string _sql;

        public SyntheticMigration(int version, string sql)
        {
            Version = version;
            _sql = sql;
        }

        public int Version { get; }

        public void Apply(SqliteConnection connection, SqliteTransaction transaction)
            => HostDatabase.Execute(connection, _sql, transaction);
    }

    public static Task TestPriorVersionFixtureAppliesOnlyTheMissingMigration()
    {
        using var temp = new TempRoot();
        using var connection = temp.Database.OpenConnection();

        var v1 = new SyntheticMigration(1, "CREATE TABLE V1State (Id INTEGER PRIMARY KEY, Marker TEXT NOT NULL);");
        var v2 = new SyntheticMigration(2, "CREATE TABLE V2State (Id INTEGER PRIMARY KEY);");

        // Establish a genuine PRIOR-VERSION database: version 1 is durably applied and carries
        // real data, and user_version is 1 - materially different from the fresh (0) path.
        var priorVersionRunner = new HostSchemaMigrationRunner([v1]);
        Equal(1, priorVersionRunner.Migrate(connection), "only v1 applied to establish the fixture");
        Execute(connection, "INSERT INTO V1State (Id, Marker) VALUES (1, 'created-by-v1');");
        Equal(1, HostSchemaMigrationRunner.ReadSchemaVersion(connection), "fixture sits at user_version 1");

        // Now a build that knows BOTH versions opens the same database.
        var upgradedRunner = new HostSchemaMigrationRunner([v1, v2]);
        Equal(2, upgradedRunner.LatestVersion, "runner knows versions 1 and 2");

        var applied = upgradedRunner.Migrate(connection);

        Equal(1, applied, "ONLY version 2 is applied - version 1 is not re-run");
        Equal(2, HostSchemaMigrationRunner.ReadSchemaVersion(connection), "user_version becomes 2");
        Equal(1L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='V2State';"), "version-2 state appears");
        // Version-1 state survives untouched - a re-run of v1 would have thrown on CREATE TABLE,
        // and its row proves the existing data was preserved rather than rebuilt.
        Equal("created-by-v1", HostDatabase.QueryScalarText(connection, "SELECT Marker FROM V1State WHERE Id = 1;"), "version-1 state remains intact");
        return Task.CompletedTask;
    }

    public static Task TestAlreadyCurrentDatabasePerformsNoMigrationWrites()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        var runner = HostSchemaMigrationRunner.Default();

        var before = HostSchemaMigrationRunner.ReadSchemaVersion(connection);
        var applied = runner.Migrate(connection);
        Equal(0, applied, "no migrations applied against a current database");
        Equal(before, HostSchemaMigrationRunner.ReadSchemaVersion(connection), "user_version unchanged");
        return Task.CompletedTask;
    }

    private sealed class ThrowingMigration : IHostSchemaMigration
    {
        public ThrowingMigration(int version) => Version = version;

        public int Version { get; }

        public void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            // Do real durable work first, then fail - so a non-transactional runner would leave
            // a partially-applied migration behind and this test would catch it.
            HostDatabase.Execute(connection, "CREATE TABLE PartiallyApplied (Id INTEGER PRIMARY KEY);", transaction);
            throw new InvalidOperationException("injected migration failure");
        }
    }

    public static Task TestFailedMigrationRollsBackFullyAndKeepsLastCommittedVersion()
    {
        using var temp = new TempRoot();
        using var connection = temp.Database.OpenConnection();

        var baseline = HostSchemaMigrationRunner.Default();
        baseline.Migrate(connection);
        var lastGood = HostSchemaMigrationRunner.ReadSchemaVersion(connection);

        var withFailure = new HostSchemaMigrationRunner(
            HostSchema.AllMigrations().Concat([new ThrowingMigration(lastGood + 1)]));

        Throws<InvalidOperationException>(() => withFailure.Migrate(connection), "failing migration must propagate");

        // The whole failing migration is rolled back...
        Equal(0L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='PartiallyApplied';"), "partially-applied table must not survive");
        // ...and user_version stays at the LAST SUCCESSFULLY COMMITTED version, never a
        // partially-applied N.
        Equal(lastGood, HostSchemaMigrationRunner.ReadSchemaVersion(connection), "user_version stays at last committed version");
        return Task.CompletedTask;
    }

    public static Task TestUnknownNewerSchemaVersionIsRejected()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        var runner = HostSchemaMigrationRunner.Default();

        Execute(connection, $"PRAGMA user_version={runner.LatestVersion + 5};");
        Throws<UnknownSchemaVersionException>(() => runner.Migrate(connection), "a newer schema must be refused, never downgraded");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- HostIdentity

    public static Task TestHostIdentityIsSingleton()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        var repo = new HostIdentityRepository(temp.Database);
        repo.EnsureHostIdentity(connection);

        // A second row is structurally impossible (CHECK (Id = 1) + PK), not merely never written.
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO HostIdentity (Id, HostId, HostBootstrapState, SetupUtc) VALUES (2, 'other', 'Uninitialized', '2026-01-01T00:00:00Z');"),
            "second HostIdentity row must be rejected");
        Equal(1L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM HostIdentity;"), "exactly one HostIdentity row");
        return Task.CompletedTask;
    }

    public static Task TestHostIdIsStableAcrossReopen()
    {
        using var temp = new TempRoot();
        var repo = new HostIdentityRepository(temp.Database);

        string first;
        using (var connection = temp.OpenMigrated())
        {
            first = repo.EnsureHostIdentity(connection).HostId;
        }

        using (var reopened = temp.OpenMigrated())
        {
            var again = repo.EnsureHostIdentity(reopened);
            Equal(first, again.HostId, "HostId must never change for this machine");
            Equal(HostBootstrapState.Uninitialized, again.BootstrapState, "bootstrap state persisted");
        }

        return Task.CompletedTask;
    }

    public static Task TestHostIdentityStoresOnlyAnOpaqueCredentialReference()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        var repo = new HostIdentityRepository(temp.Database);
        repo.EnsureHostIdentity(connection);

        // CurrentCredentialRef is an opaque pointer into ISecureCredentialStore (SS7), and the
        // referenced row carries only metadata - never key bytes.
        Execute(connection, "INSERT INTO SecureCredentialReferences (CredentialRef, Purpose, CreatedUtc) VALUES ('cred-ref-1', 'HostIdentity', '2026-01-01T00:00:00Z');");
        Execute(connection, "UPDATE HostIdentity SET CurrentCredentialRef = 'cred-ref-1' WHERE Id = 1;");

        var identity = repo.TryReadHostIdentity(connection)!;
        Equal("cred-ref-1", identity.CurrentCredentialRef, "opaque credential reference round-trips");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- LocalPrincipals / Owner

    public static Task TestDuplicateOsPrincipalRefIsRejected()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();

        Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('p1', 'S-1-5-21-A', 'pub1', 0, 'Active', '2026-01-01T00:00:00Z');");
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('p2', 'S-1-5-21-A', 'pub2', 0, 'Active', '2026-01-01T00:00:00Z');"),
            "one LocalPrincipal per OsPrincipalRef");
        return Task.CompletedTask;
    }

    public static Task TestAtMostOneActiveOwnerIsADatabaseConstraint()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();

        Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('owner1', 'S-1-5-21-A', 'pub1', 1, 'Active', '2026-01-01T00:00:00Z');");
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('owner2', 'S-1-5-21-B', 'pub2', 1, 'Active', '2026-01-01T00:00:00Z');"),
            "a second ACTIVE Owner must be rejected by the database");

        // A revoked Owner tombstone does not block a new active Owner (LOCAL-003 retention).
        Execute(connection, "UPDATE LocalPrincipals SET State='Revoked', PublicVerificationKey=NULL, RevokedUtc='2026-01-02T00:00:00Z' WHERE LocalPrincipalId='owner1';");
        Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('owner2', 'S-1-5-21-B', 'pub2', 1, 'Active', '2026-01-02T00:00:00Z');");
        Equal(1, HostIdentityRepository.CountActiveOwners(connection), "exactly one active Owner after tombstoned handover");
        Equal(2L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM LocalPrincipals;"), "revoked row retained as a tombstone, never deleted");
        return Task.CompletedTask;
    }

    public static Task TestRevokedPrincipalCannotRetainVerificationKey()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();

        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('p1', 'S-1-5-21-A', 'still-usable', 0, 'Revoked', '2026-01-01T00:00:00Z');"),
            "a Revoked principal must not retain a usable key (LOCAL-003)");
        return Task.CompletedTask;
    }

    public static Task TestUninitializedHostHasZeroActiveOwners()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        var repo = new HostIdentityRepository(temp.Database);

        var identity = repo.EnsureHostIdentity(connection);
        Equal(HostBootstrapState.Uninitialized, identity.BootstrapState, "brand-new Host is Uninitialized");
        Equal(0, HostIdentityRepository.CountActiveOwners(connection), "Uninitialized commits with zero active Owners");
        return Task.CompletedTask;
    }

    public static Task TestInitializedTransitionRequiresExactlyOneActiveOwnerAtomically()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        var repo = new HostIdentityRepository(temp.Database);
        repo.EnsureHostIdentity(connection);

        using (var transaction = connection.BeginTransaction())
        {
            repo.InitializeWithOwner(connection, transaction, "owner-1", "S-1-5-21-OWNER", "owner-public-key");
            transaction.Commit();
        }

        var identity = repo.TryReadHostIdentity(connection)!;
        Equal(HostBootstrapState.Initialized, identity.BootstrapState, "Host became Initialized");
        Equal(1, HostIdentityRepository.CountActiveOwners(connection), "exactly one active Owner");

        // Never re-initialized, never reverts (SS2c).
        Throws<InvalidOperationException>(
            () =>
            {
                using var retry = connection.BeginTransaction();
                repo.InitializeWithOwner(connection, retry, "owner-2", "S-1-5-21-OTHER", "other-key");
                retry.Commit();
            },
            "an Initialized Host is never re-initialized");
        Equal(1, HostIdentityRepository.CountActiveOwners(connection), "still exactly one active Owner after the rejected attempt");
        return Task.CompletedTask;
    }

    public static Task TestNoObservableInitializedStateWithoutAnOwner()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        var repo = new HostIdentityRepository(temp.Database);
        repo.EnsureHostIdentity(connection);

        // Force the failure path: an Owner row that violates the exactly-one rule inside the
        // same transaction as the state transition must abort the whole transition.
        Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('pre', 'S-1-5-21-PRE', 'k', 1, 'Active', '2026-01-01T00:00:00Z');");

        Throws<SqliteException>(
            () =>
            {
                using var conflicting = connection.BeginTransaction();
                repo.InitializeWithOwner(connection, conflicting, "owner-x", "S-1-5-21-X", "kx");
                conflicting.Commit();
            },
            "a conflicting active Owner aborts the transition");

        // The Host must still be Uninitialized - no committed Initialized-without-exactly-one-Owner
        // state was ever observable.
        Equal(HostBootstrapState.Uninitialized, repo.TryReadHostIdentity(connection)!.BootstrapState, "transition rolled back");
        return Task.CompletedTask;
    }

    // Correction 6: the caller owns the atomic unit, so rolling back must discard BOTH the Owner
    // row and the bootstrap-state transition - proving the primitive never commits on its own.
    public static Task TestOwnerInitializationIsTransactionComposableAndRollsBack()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        var repo = new HostIdentityRepository(temp.Database);
        repo.EnsureHostIdentity(connection);

        using (var transaction = connection.BeginTransaction())
        {
            repo.InitializeWithOwner(connection, transaction, "owner-rb", "S-1-5-21-RB", "key-rb");

            // Visible inside the caller's own transaction before it decides to commit.
            Equal(1, HostIdentityRepository.CountActiveOwners(connection, transaction), "Owner visible inside the open transaction");
            transaction.Rollback();
        }

        // Both effects are gone - the primitive committed nothing by itself.
        Equal(HostBootstrapState.Uninitialized, repo.TryReadHostIdentity(connection)!.BootstrapState, "bootstrap transition rolled back");
        Equal(0, HostIdentityRepository.CountActiveOwners(connection), "Owner creation rolled back");
        Equal(0L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM LocalPrincipals;"), "no Owner row survives the caller's rollback");

        // And the caller can compose additional work into one atomic unit, exactly as SS2c's real
        // bootstrap ceremony will need to (#42).
        using (var transaction = connection.BeginTransaction())
        {
            repo.InitializeWithOwner(connection, transaction, "owner-ok", "S-1-5-21-OK", "key-ok");
            using (var audit = connection.CreateCommand())
            {
                audit.Transaction = transaction;
                audit.CommandText = "INSERT INTO AuditEvents (AuditEventId, OccurredUtc, EventKind, ActorKind, IsOfflineRecovery) VALUES ('a-boot', '2026-01-01T00:00:00Z', 'OwnerBootstrapCompleted', 'OfflineRecovery', 1);";
                audit.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        Equal(HostBootstrapState.Initialized, repo.TryReadHostIdentity(connection)!.BootstrapState, "composed commit succeeded");
        Equal(1, HostIdentityRepository.CountActiveOwners(connection), "exactly one active Owner");
        Equal(1L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM AuditEvents WHERE AuditEventId='a-boot';"), "caller-composed audit committed in the same unit");
        return Task.CompletedTask;
    }

    // Correction 4: BOTH invalid key/state combinations must be rejected.
    public static Task TestActivePrincipalMustHaveAVerificationKey()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();

        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('p1', 'S-1-5-21-A', NULL, 0, 'Active', '2026-01-01T00:00:00Z');"),
            "an Active principal with no key must be rejected");
        return Task.CompletedTask;
    }

    // Correction 1: the Owner principal that created an enrollment is persisted, with FK integrity.
    public static Task TestEnrollmentPersistsItsCreatingOwnerPrincipal()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('owner1', 'S-1-5-21-OWNER', 'k', 1, 'Active', '2026-01-01T00:00:00Z');");

        Execute(connection, "INSERT INTO PendingLocalPrincipalEnrollments (EnrollmentId, OsPrincipalRef, EnrollmentCodeVerifier, CreatedByOwnerLocalPrincipalId, ExpiresUtc, CreatedUtc) VALUES ('e1', 'S-1-5-21-NEW', 'verifier', 'owner1', '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');");
        Equal("owner1", HostDatabase.QueryScalarText(connection, "SELECT CreatedByOwnerLocalPrincipalId FROM PendingLocalPrincipalEnrollments WHERE EnrollmentId='e1';"), "creating Owner persisted");

        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO PendingLocalPrincipalEnrollments (EnrollmentId, OsPrincipalRef, EnrollmentCodeVerifier, CreatedByOwnerLocalPrincipalId, ExpiresUtc, CreatedUtc) VALUES ('e2', 'S-1-5-21-NEW2', 'verifier', 'no-such-owner', '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');"),
            "a dangling creator principal must be rejected");

        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO PendingLocalPrincipalEnrollments (EnrollmentId, OsPrincipalRef, EnrollmentCodeVerifier, ExpiresUtc, CreatedUtc) VALUES ('e3', 'S-1-5-21-NEW3', 'verifier', '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');"),
            "an enrollment with no recorded creator must be rejected");
        return Task.CompletedTask;
    }

    // Correction 3: at most one LIVE initial-Owner ticket, while history is retained.
    public static Task TestOnlyOneLiveInitialOwnerEnrollmentMayExist()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();

        Execute(connection, "INSERT INTO PendingOwnerEnrollments (PendingOwnerEnrollmentId, OsPrincipalRef, SecretVerifier, ExpiresUtc, CreatedUtc) VALUES ('b1', 'S-1-5-21-A', 'v1', '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');");

        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO PendingOwnerEnrollments (PendingOwnerEnrollmentId, OsPrincipalRef, SecretVerifier, ExpiresUtc, CreatedUtc) VALUES ('b2', 'S-1-5-21-B', 'v2', '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');"),
            "a second simultaneously-live bootstrap ticket must be rejected");

        // Consumed history does not block a later live ticket, and is retained (SS2c's
        // idempotent-retry rule depends on that retention).
        Execute(connection, "UPDATE PendingOwnerEnrollments SET ConsumedUtc='2026-01-02T00:00:00Z' WHERE PendingOwnerEnrollmentId='b1';");
        Execute(connection, "INSERT INTO PendingOwnerEnrollments (PendingOwnerEnrollmentId, OsPrincipalRef, SecretVerifier, ExpiresUtc, CreatedUtc) VALUES ('b2', 'S-1-5-21-B', 'v2', '2030-01-01T00:00:00Z', '2026-01-02T00:00:00Z');");

        // Explicitly invalidated history likewise does not block.
        Execute(connection, "UPDATE PendingOwnerEnrollments SET InvalidatedUtc='2026-01-03T00:00:00Z' WHERE PendingOwnerEnrollmentId='b2';");
        Execute(connection, "INSERT INTO PendingOwnerEnrollments (PendingOwnerEnrollmentId, OsPrincipalRef, SecretVerifier, ExpiresUtc, CreatedUtc) VALUES ('b3', 'S-1-5-21-C', 'v3', '2030-01-01T00:00:00Z', '2026-01-03T00:00:00Z');");

        Equal(3L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM PendingOwnerEnrollments;"), "history rows retained, never deleted");
        return Task.CompletedTask;
    }

    // Correction 2: the full accepted PendingCredentialReplacement shape.
    public static Task TestPendingCredentialReplacementCapturesExpectedTrustSnapshot()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('owner1', 'S-1-5-21-OWNER', 'k', 1, 'Active', '2026-01-01T00:00:00Z');");
        Execute(connection, "INSERT INTO TrustedManagers (PeerHostId, State, CurrentTrustedPublicKeyFingerprint, CreatedUtc) VALUES ('peer-A', 'Active', 'fp-1', '2026-01-01T00:00:00Z');");

        Execute(connection, "INSERT INTO PendingCredentialReplacements (ReplacementId, PeerHostId, ProposedKeyFingerprint, VerifiedUtc, ExpectedTrustState, ExpectedCurrentTrustedPublicKeyFingerprint, ExpiresUtc, CreatedUtc) VALUES ('r1', 'peer-A', 'fp-2', '2026-01-01T00:00:00Z', 'Active', 'fp-1', '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');");
        Equal("Active", HostDatabase.QueryScalarText(connection, "SELECT ExpectedTrustState FROM PendingCredentialReplacements WHERE ReplacementId='r1';"), "expected trust state round-trips");
        Equal("fp-1", HostDatabase.QueryScalarText(connection, "SELECT ExpectedCurrentTrustedPublicKeyFingerprint FROM PendingCredentialReplacements WHERE ReplacementId='r1';"), "expected fingerprint round-trips");

        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO PendingCredentialReplacements (ReplacementId, PeerHostId, ProposedKeyFingerprint, VerifiedUtc, ExpectedTrustState, ExpectedCurrentTrustedPublicKeyFingerprint, ExpiresUtc, CreatedUtc) VALUES ('r2', 'peer-A', 'fp-3', '2026-01-01T00:00:00Z', 'NotARealState', 'fp-1', '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');"),
            "an invalid ExpectedTrustState must be rejected");

        // Approval metadata is a single fact: both halves or neither.
        Throws<SqliteException>(
            () => Execute(connection, "UPDATE PendingCredentialReplacements SET ApprovedUtc='2026-01-02T00:00:00Z' WHERE ReplacementId='r1';"),
            "approval time without an approving Owner must be rejected");
        Throws<SqliteException>(
            () => Execute(connection, "UPDATE PendingCredentialReplacements SET ApprovedByOwnerLocalPrincipalId='owner1' WHERE ReplacementId='r1';"),
            "approving Owner without an approval time must be rejected");
        Throws<SqliteException>(
            () => Execute(connection, "UPDATE PendingCredentialReplacements SET ApprovedUtc='2026-01-02T00:00:00Z', ApprovedByOwnerLocalPrincipalId='no-such-owner' WHERE ReplacementId='r1';"),
            "a dangling approving Owner must be rejected");

        Execute(connection, "UPDATE PendingCredentialReplacements SET ApprovedUtc='2026-01-02T00:00:00Z', ApprovedByOwnerLocalPrincipalId='owner1' WHERE ReplacementId='r1';");
        Equal("owner1", HostDatabase.QueryScalarText(connection, "SELECT ApprovedByOwnerLocalPrincipalId FROM PendingCredentialReplacements WHERE ReplacementId='r1';"), "complete approval metadata accepted");

        // SS4b-i decided PendingRotationId is NOT captured as an expected snapshot value.
        Equal(0L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM pragma_table_info('PendingCredentialReplacements') WHERE name='ExpectedPendingRotationId';"), "PendingRotationId is deliberately not captured");
        return Task.CompletedTask;
    }

    // Correction 5: the complete tombstone constraint, all six cleared fields.
    public static Task TestTrustedManagerStateAndCredentialCombinationsAreConstrained()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();

        // A pinned relationship must actually carry a pin.
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO TrustedManagers (PeerHostId, State, CreatedUtc) VALUES ('p1', 'Active', '2026-01-01T00:00:00Z');"),
            "Active without a current fingerprint must be rejected");
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO TrustedManagers (PeerHostId, State, CreatedUtc) VALUES ('p2', 'PeerBound', '2026-01-01T00:00:00Z');"),
            "PeerBound without a current fingerprint must be rejected");

        // Every field a revocation must clear.
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO TrustedManagers (PeerHostId, State, CurrentTrustedPublicKeyFingerprint, CreatedUtc) VALUES ('r1', 'Revoked', 'fp', '2026-01-01T00:00:00Z');"),
            "Revoked with a current fingerprint must be rejected");
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO TrustedManagers (PeerHostId, State, PendingTrustedPublicKeyFingerprint, CreatedUtc) VALUES ('r2', 'Revoked', 'fp-pending', '2026-01-01T00:00:00Z');"),
            "Revoked with a pending fingerprint must be rejected");
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO TrustedManagers (PeerHostId, State, PendingRotationId, CreatedUtc) VALUES ('r3', 'Revoked', 'rot-1', '2026-01-01T00:00:00Z');"),
            "Revoked with a pending rotation id must be rejected");
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO TrustedManagers (PeerHostId, State, PendingRotationExpiresUtc, CreatedUtc) VALUES ('r4', 'Revoked', '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');"),
            "Revoked with a pending rotation expiry must be rejected");
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO TrustedManagers (PeerHostId, State, PendingReconfirmationRequired, CreatedUtc) VALUES ('r5', 'Revoked', 1, '2026-01-01T00:00:00Z');"),
            "Revoked with PendingReconfirmationRequired must be rejected");
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO TrustedManagers (PeerHostId, State, PeerRecoveryRequired, CreatedUtc) VALUES ('r6', 'Revoked', 1, '2026-01-01T00:00:00Z');"),
            "Revoked with PeerRecoveryRequired must be rejected");

        // A clean tombstone is accepted and retained.
        Execute(connection, "INSERT INTO TrustedManagers (PeerHostId, State, RevokedUtc, CreatedUtc) VALUES ('clean', 'Revoked', '2026-01-02T00:00:00Z', '2026-01-01T00:00:00Z');");
        Equal(1L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM TrustedManagers WHERE PeerHostId='clean';"), "a clean Revoked tombstone is accepted");

        // SS4a-i: PendingRotationId alone legitimately survives promotion on a live row.
        Execute(connection, "INSERT INTO TrustedManagers (PeerHostId, State, CurrentTrustedPublicKeyFingerprint, PendingRotationId, CreatedUtc) VALUES ('promoted', 'Active', 'fp-new', 'rot-9', '2026-01-01T00:00:00Z');");
        Equal("rot-9", HostDatabase.QueryScalarText(connection, "SELECT PendingRotationId FROM TrustedManagers WHERE PeerHostId='promoted';"), "a promoted peer may retain PendingRotationId alone");
        return Task.CompletedTask;
    }

    // SS4a's CredentialHistory[]: each peer's prior credentials as observed by this Host.
    public static Task TestTrustedManagerCredentialHistoryIsRetainedPerPeer()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();

        Execute(connection, "INSERT INTO TrustedManagers (PeerHostId, State, CurrentTrustedPublicKeyFingerprint, CreatedUtc) VALUES ('peer-A', 'Active', 'fp-A3', '2026-01-01T00:00:00Z');");
        Execute(connection, "INSERT INTO TrustedManagers (PeerHostId, State, CurrentTrustedPublicKeyFingerprint, CreatedUtc) VALUES ('peer-B', 'Active', 'fp-B2', '2026-01-01T00:00:00Z');");

        // More than one prior fingerprint is retained for a single peer.
        Execute(connection, "INSERT INTO TrustedManagerCredentialHistory (CredentialHistoryId, PeerHostId, PriorPublicKeyFingerprint, RotatedUtc) VALUES ('h1', 'peer-A', 'fp-A1', '2026-01-02T00:00:00Z');");
        Execute(connection, "INSERT INTO TrustedManagerCredentialHistory (CredentialHistoryId, PeerHostId, PriorPublicKeyFingerprint, RotatedUtc) VALUES ('h2', 'peer-A', 'fp-A2', '2026-01-03T00:00:00Z');");
        Execute(connection, "INSERT INTO TrustedManagerCredentialHistory (CredentialHistoryId, PeerHostId, PriorPublicKeyFingerprint, RotatedUtc) VALUES ('h3', 'peer-B', 'fp-B1', '2026-01-02T00:00:00Z');");

        Equal(2L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM TrustedManagerCredentialHistory WHERE PeerHostId='peer-A';"), "multiple prior fingerprints retained for one peer");
        // Histories stay distinct per peer.
        Equal(1L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM TrustedManagerCredentialHistory WHERE PeerHostId='peer-B';"), "another peer's history is separate");
        Equal("fp-B1", HostDatabase.QueryScalarText(connection, "SELECT PriorPublicKeyFingerprint FROM TrustedManagerCredentialHistory WHERE PeerHostId='peer-B';"), "peer-B history holds only its own prior fingerprint");

        // FK integrity.
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO TrustedManagerCredentialHistory (CredentialHistoryId, PeerHostId, PriorPublicKeyFingerprint, RotatedUtc) VALUES ('h9', 'no-such-peer', 'fp-x', '2026-01-02T00:00:00Z');"),
            "a dangling PeerHostId must be rejected");

        // History survives the peer's current fingerprint changing (that is the point of it).
        Execute(connection, "UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='fp-A4' WHERE PeerHostId='peer-A';");
        Equal(2L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM TrustedManagerCredentialHistory WHERE PeerHostId='peer-A';"), "history survives a current-fingerprint change");

        // Audit-only: fingerprints and timestamps, never key material, and (SS4a-i step 6) never
        // a RotationId.
        var suspicious = HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM pragma_table_info('TrustedManagerCredentialHistory') WHERE name LIKE '%PrivateKey%' OR name LIKE '%Secret%' OR name='RotationId';");
        Equal(0L, suspicious, "history carries no secret material and no RotationId");
        return Task.CompletedTask;
    }

    // SS2b: the current-Owner stale-check snapshots are mandatory on both recovery tickets.
    public static Task TestOwnerRecoveryTicketsRequireCurrentOwnerSnapshots()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('owner1', 'S-1-5-21-OWNER', 'owner-key', 1, 'Active', '2026-01-01T00:00:00Z');");

        // Rotation ticket: the captured current key IS the stale-ticket check, never optional.
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO PendingOwnerCredentialRotations (RotationTicketId, LocalPrincipalId, OsPrincipalRef, SecretVerifier, ExpiresUtc, CreatedUtc) VALUES ('t1', 'owner1', 'S-1-5-21-OWNER', 'v', '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');"),
            "a rotation ticket without the current-key snapshot must be rejected");

        Execute(connection, "INSERT INTO PendingOwnerCredentialRotations (RotationTicketId, LocalPrincipalId, OsPrincipalRef, SecretVerifier, ExpectedCurrentPublicVerificationKey, ExpiresUtc, CreatedUtc) VALUES ('t1', 'owner1', 'S-1-5-21-OWNER', 'v', 'owner-key', '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');");
        Equal("owner-key", HostDatabase.QueryScalarText(connection, "SELECT ExpectedCurrentPublicVerificationKey FROM PendingOwnerCredentialRotations WHERE RotationTicketId='t1';"), "rotation snapshot round-trips");

        // Re-home: both current-Owner components are mandatory.
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO PendingOwnerRehomes (RehomeTicketId, NewOsPrincipalRef, SecretVerifier, ExpectedCurrentOwnerPublicVerificationKey, ExpiresUtc, CreatedUtc) VALUES ('h1', 'S-1-5-21-NEW', 'v', 'owner-key', '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');"),
            "a re-home without the current-Owner principal id must be rejected");
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO PendingOwnerRehomes (RehomeTicketId, NewOsPrincipalRef, SecretVerifier, ExpectedCurrentOwnerLocalPrincipalId, ExpiresUtc, CreatedUtc) VALUES ('h2', 'S-1-5-21-NEW', 'v', 'owner1', '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');"),
            "a re-home without the current-Owner key must be rejected");
        return Task.CompletedTask;
    }

    // SS2b's two valid target-snapshot shapes.
    public static Task TestRehomeTargetSnapshotTupleIsCoherent()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('owner1', 'S-1-5-21-OWNER', 'owner-key', 1, 'Active', '2026-01-01T00:00:00Z');");
        Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('tgtA', 'S-1-5-21-TGTA', 'tgt-key', 0, 'Active', '2026-01-01T00:00:00Z');");
        Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc, RevokedUtc) VALUES ('tgtR', 'S-1-5-21-TGTR', NULL, 0, 'Revoked', '2026-01-01T00:00:00Z', '2026-01-02T00:00:00Z');");

        const string Cols = "(RehomeTicketId, NewOsPrincipalRef, SecretVerifier, ExpectedCurrentOwnerLocalPrincipalId, ExpectedCurrentOwnerPublicVerificationKey, ExpectedTargetLocalPrincipalId, ExpectedTargetState, ExpectedTargetPublicVerificationKey, ExpiresUtc, CreatedUtc)";
        const string Tail = ", '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');";

        // 1. brand-new account: all three null.
        Execute(connection, $"INSERT INTO PendingOwnerRehomes {Cols} VALUES ('ok-null', 'S-1-5-21-NEW', 'v', 'owner1', 'owner-key', NULL, NULL, NULL{Tail}");
        // 2a. existing Active target carries its key.
        Execute(connection, $"INSERT INTO PendingOwnerRehomes {Cols} VALUES ('ok-active', 'S-1-5-21-TGTA', 'v', 'owner1', 'owner-key', 'tgtA', 'Active', 'tgt-key'{Tail}");
        // 2b. existing Revoked target has none.
        Execute(connection, $"INSERT INTO PendingOwnerRehomes {Cols} VALUES ('ok-revoked', 'S-1-5-21-TGTR', 'v', 'owner1', 'owner-key', 'tgtR', 'Revoked', NULL{Tail}");
        Equal(3L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM PendingOwnerRehomes;"), "all three valid target shapes accepted");

        // Partial tuples and state/key disagreement are rejected.
        Throws<SqliteException>(
            () => Execute(connection, $"INSERT INTO PendingOwnerRehomes {Cols} VALUES ('bad-partial-id', 'S-1-5-21-X', 'v', 'owner1', 'owner-key', 'tgtA', NULL, NULL{Tail}"),
            "target id without a captured state must be rejected");
        Throws<SqliteException>(
            () => Execute(connection, $"INSERT INTO PendingOwnerRehomes {Cols} VALUES ('bad-partial-state', 'S-1-5-21-X', 'v', 'owner1', 'owner-key', NULL, 'Active', NULL{Tail}"),
            "captured state without a target id must be rejected");
        Throws<SqliteException>(
            () => Execute(connection, $"INSERT INTO PendingOwnerRehomes {Cols} VALUES ('bad-active-nokey', 'S-1-5-21-X', 'v', 'owner1', 'owner-key', 'tgtA', 'Active', NULL{Tail}"),
            "an Active target snapshot with no key must be rejected");
        Throws<SqliteException>(
            () => Execute(connection, $"INSERT INTO PendingOwnerRehomes {Cols} VALUES ('bad-revoked-key', 'S-1-5-21-X', 'v', 'owner1', 'owner-key', 'tgtR', 'Revoked', 'leftover-key'{Tail}"),
            "a Revoked target snapshot carrying a key must be rejected");
        return Task.CompletedTask;
    }

    // The replacement snapshot mirrors TrustedManagers' own valid combinations.
    public static Task TestReplacementExpectedTrustTupleMirrorsTrustedManagers()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        Execute(connection, "INSERT INTO TrustedManagers (PeerHostId, State, CurrentTrustedPublicKeyFingerprint, CreatedUtc) VALUES ('peer-A', 'Active', 'fp-1', '2026-01-01T00:00:00Z');");

        const string Cols = "(ReplacementId, PeerHostId, ProposedKeyFingerprint, VerifiedUtc, ExpectedTrustState, ExpectedCurrentTrustedPublicKeyFingerprint, ExpiresUtc, CreatedUtc)";
        const string Tail = ", '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');";

        Execute(connection, $"INSERT INTO PendingCredentialReplacements {Cols} VALUES ('ok-active', 'peer-A', 'fp-2', '2026-01-01T00:00:00Z', 'Active', 'fp-1'{Tail}");
        Execute(connection, $"INSERT INTO PendingCredentialReplacements {Cols} VALUES ('ok-peerbound', 'peer-A', 'fp-2', '2026-01-01T00:00:00Z', 'PeerBound', 'fp-0'{Tail}");
        Execute(connection, $"INSERT INTO PendingCredentialReplacements {Cols} VALUES ('ok-revoked', 'peer-A', 'fp-2', '2026-01-01T00:00:00Z', 'Revoked', NULL{Tail}");
        Equal(3L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM PendingCredentialReplacements;"), "all three coherent snapshots accepted");

        Throws<SqliteException>(
            () => Execute(connection, $"INSERT INTO PendingCredentialReplacements {Cols} VALUES ('bad-active', 'peer-A', 'fp-2', '2026-01-01T00:00:00Z', 'Active', NULL{Tail}"),
            "an Active snapshot with no pinned fingerprint must be rejected");
        Throws<SqliteException>(
            () => Execute(connection, $"INSERT INTO PendingCredentialReplacements {Cols} VALUES ('bad-peerbound', 'peer-A', 'fp-2', '2026-01-01T00:00:00Z', 'PeerBound', NULL{Tail}"),
            "a PeerBound snapshot with no pinned fingerprint must be rejected");
        Throws<SqliteException>(
            () => Execute(connection, $"INSERT INTO PendingCredentialReplacements {Cols} VALUES ('bad-revoked', 'peer-A', 'fp-2', '2026-01-01T00:00:00Z', 'Revoked', 'fp-leftover'{Tail}"),
            "a Revoked snapshot carrying a fingerprint must be rejected");
        return Task.CompletedTask;
    }

    // SS8 reconciliation: 'Prepared' is a real nonterminal rotation state.
    public static Task TestHostCredentialRotationSupportsPreparedState()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();

        Execute(connection, "INSERT INTO HostCredentialRotations (RotationId, State, StartedUtc) VALUES ('rot-1', 'Prepared', '2026-01-01T00:00:00Z');");
        Equal("Prepared", HostDatabase.QueryScalarText(connection, "SELECT State FROM HostCredentialRotations WHERE RotationId='rot-1';"), "Prepared is representable");

        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO HostCredentialRotations (RotationId, State, StartedUtc) VALUES ('rot-2', 'NotARealState', '2026-01-01T00:00:00Z');"),
            "an unknown rotation state must be rejected");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- secret boundary

    public static Task TestSchemaHasNoRawSecretPersistenceFields()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();

        // SS7 / SEC-001 at the ordinary-persistence boundary: the schema may hold public keys,
        // keyed verifiers, and opaque references - but no column may be shaped to persist raw
        // private-key or reusable bearer-secret material.
        var forbidden = new[]
        {
            "PrivateKey", "PrivateVerificationKey", "SecretValue", "RawSecret",
            "EnrollmentCode", "OwnerBootstrapSecret", "CredentialBytes", "KeyMaterial",
        };

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT m.name, p.name FROM sqlite_master m JOIN pragma_table_info(m.name) p WHERE m.type='table';";
        using var reader = command.ExecuteReader();

        var offenders = new List<string>();
        while (reader.Read())
        {
            var table = reader.GetString(0);
            var column = reader.GetString(1);

            foreach (var bad in forbidden)
            {
                // Exact-name match: "EnrollmentCodeVerifier" is explicitly ALLOWED (it is a keyed
                // verifier, not the raw code), while a bare "EnrollmentCode" column is not.
                if (string.Equals(column, bad, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{table}.{column}");
                }
            }
        }

        True(offenders.Count == 0, $"no raw secret persistence fields; found: {string.Join(", ", offenders)}");
        return Task.CompletedTask;
    }

    public static Task TestVerifierAndPublicKeyPersistenceIsAllowed()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();

        // What SS19 explicitly permits in ordinary Host state.
        Execute(connection, "INSERT INTO PendingOwnerEnrollments (PendingOwnerEnrollmentId, OsPrincipalRef, SecretVerifier, ExpiresUtc, CreatedUtc) VALUES ('e1', 'S-1-5-21-A', 'keyed-verifier-not-the-secret', '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');");
        Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('p1', 'S-1-5-21-A', 'public-verification-key', 0, 'Active', '2026-01-01T00:00:00Z');");
        Execute(connection, "INSERT INTO SecureCredentialReferences (CredentialRef, Purpose, CreatedUtc) VALUES ('ref-1', 'HostIdentity', '2026-01-01T00:00:00Z');");

        Equal(1L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM PendingOwnerEnrollments;"), "verifier row persisted");
        Equal(1L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM SecureCredentialReferences;"), "opaque reference persisted");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- transactions

    public static Task TestTransactionRollbackDiscardsAllWrites()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();

        using (var transaction = connection.BeginTransaction())
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('rollback-me', 'S-1-5-21-R', 'k', 0, 'Active', '2026-01-01T00:00:00Z');";
            insert.ExecuteNonQuery();
            transaction.Rollback();
        }

        Equal(0L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM LocalPrincipals;"), "rolled-back write must not persist");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- identity/trust/grants

    public static Task TestServerInventoryIsHostQualified()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();

        // IDENT-002: AuthoritativeHostId is never omitted, even for this Host's own servers.
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO ServerInventory (ServerProfileId, DisplayName, CreatedUtc) VALUES ('s1', 'My Server', '2026-01-01T00:00:00Z');"),
            "an unqualified server row must be rejected");

        Execute(connection, "INSERT INTO ServerInventory (ServerProfileId, AuthoritativeHostId, DisplayName, CreatedUtc) VALUES ('s1', 'host-A', 'My Server', '2026-01-01T00:00:00Z');");
        // The same ServerProfileId under a different Host is a genuinely different ServerRef.
        Execute(connection, "INSERT INTO ServerInventory (ServerProfileId, AuthoritativeHostId, DisplayName, CreatedUtc) VALUES ('s1', 'host-B', 'Peer Server', '2026-01-01T00:00:00Z');");
        Equal(2L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM ServerInventory;"), "ServerRef identity is Host-qualified");
        return Task.CompletedTask;
    }

    public static Task TestHostCapabilityGrantRequiresExactlyOneTargetHostId()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('p1', 'S-1-5-21-A', 'k', 1, 'Active', '2026-01-01T00:00:00Z');");

        // AUTH-005: every HostCapabilityGrant targets exactly one HostId.
        Throws<SqliteException>(
            () => Execute(connection, """
                INSERT INTO HostCapabilityGrants
                    (GrantId, Capability, GranteeActorKind, GranteeLocalPrincipalId, GrantedByActorKind, GrantedByLocalPrincipalId, CanDelegate, CanDelegateOnwardDelegation, CreatedUtc)
                VALUES ('g1', 'ManageHostUpdates', 'LocalPrincipal', 'p1', 'LocalPrincipal', 'p1', 0, 0, '2026-01-01T00:00:00Z');
                """),
            "a HostCapabilityGrant without TargetHostId must be rejected");

        Execute(connection, """
            INSERT INTO HostCapabilityGrants
                (GrantId, TargetHostId, Capability, GranteeActorKind, GranteeLocalPrincipalId, GrantedByActorKind, GrantedByLocalPrincipalId, CanDelegate, CanDelegateOnwardDelegation, CreatedUtc)
            VALUES ('g1', 'host-A', 'ManageHostUpdates', 'LocalPrincipal', 'p1', 'LocalPrincipal', 'p1', 0, 0, '2026-01-01T00:00:00Z');
            """);
        Equal("host-A", HostDatabase.QueryScalarText(connection, "SELECT TargetHostId FROM HostCapabilityGrants WHERE GrantId='g1';"), "TargetHostId persisted");
        return Task.CompletedTask;
    }

    public static Task TestGrantTypesAreStructurallyDistinct()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();

        // AUTH-001: Host- and server-capability grants live in separate tables, so a Host-level
        // capability can never BE a server-scoped grant - type/scope valid by construction.
        var hostCols = HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM pragma_table_info('HostCapabilityGrants') WHERE name='TargetHostId';");
        var serverHasTargetHostId = HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM pragma_table_info('ServerCapabilityGrants') WHERE name='TargetHostId';");
        var serverHasServerProfile = HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM pragma_table_info('ServerCapabilityGrants') WHERE name='ServerProfileId';");

        Equal(1L, hostCols, "HostCapabilityGrants carries TargetHostId");
        Equal(0L, serverHasTargetHostId, "ServerCapabilityGrants does not carry a Host target");
        Equal(1L, serverHasServerProfile, "ServerCapabilityGrants is server-scoped");
        return Task.CompletedTask;
    }

    public static Task TestGrantDelegationProvenanceIsSingleParent()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('p1', 'S-1-5-21-A', 'k', 1, 'Active', '2026-01-01T00:00:00Z');");
        Execute(connection, """
            INSERT INTO HostCapabilityGrants
                (GrantId, TargetHostId, Capability, GranteeActorKind, GranteeLocalPrincipalId, GrantedByActorKind, GrantedByLocalPrincipalId, CanDelegate, CanDelegateOnwardDelegation, CreatedUtc)
            VALUES ('root', 'host-A', 'ManageHostUpdates', 'LocalPrincipal', 'p1', 'LocalPrincipal', 'p1', 1, 1, '2026-01-01T00:00:00Z');
            """);

        // AUTH-002: exactly one provenance parent - a single-parent forest, never a DAG.
        Execute(connection, """
            INSERT INTO HostCapabilityGrants
                (GrantId, TargetHostId, Capability, GranteeActorKind, GranteeLocalPrincipalId, GrantedByActorKind, GrantedByLocalPrincipalId, CanDelegate, CanDelegateOnwardDelegation, DerivedFromGrantId, CreatedUtc)
            VALUES ('child', 'host-A', 'ManageHostUpdates', 'LocalPrincipal', 'p1', 'LocalPrincipal', 'p1', 0, 0, 'root', '2026-01-01T00:00:00Z');
            """);
        Equal("root", HostDatabase.QueryScalarText(connection, "SELECT DerivedFromGrantId FROM HostCapabilityGrants WHERE GrantId='child';"), "single provenance parent recorded");

        // A dangling provenance parent is rejected by the FK.
        Throws<SqliteException>(
            () => Execute(connection, """
                INSERT INTO HostCapabilityGrants
                    (GrantId, TargetHostId, Capability, GranteeActorKind, GranteeLocalPrincipalId, GrantedByActorKind, GrantedByLocalPrincipalId, CanDelegate, CanDelegateOnwardDelegation, DerivedFromGrantId, CreatedUtc)
                VALUES ('orphan', 'host-A', 'ManageHostUpdates', 'LocalPrincipal', 'p1', 'LocalPrincipal', 'p1', 0, 0, 'no-such-grant', '2026-01-01T00:00:00Z');
                """),
            "provenance must reference a real parent grant");

        // Onward-delegation authority can never exceed delegation authority (SS5).
        Throws<SqliteException>(
            () => Execute(connection, """
                INSERT INTO HostCapabilityGrants
                    (GrantId, TargetHostId, Capability, GranteeActorKind, GranteeLocalPrincipalId, GrantedByActorKind, GrantedByLocalPrincipalId, CanDelegate, CanDelegateOnwardDelegation, CreatedUtc)
                VALUES ('bad', 'host-A', 'ManageHostUpdates', 'LocalPrincipal', 'p1', 'LocalPrincipal', 'p1', 0, 1, '2026-01-01T00:00:00Z');
                """),
            "CanDelegateOnwardDelegation requires CanDelegate");
        return Task.CompletedTask;
    }

    public static Task TestTrustedManagerTombstoneClearsPinnedCredential()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();

        Execute(connection, "INSERT INTO TrustedManagers (PeerHostId, State, CurrentTrustedPublicKeyFingerprint, CreatedUtc) VALUES ('peer-A', 'Active', 'fp-1', '2026-01-01T00:00:00Z');");

        // SS8: a Revoked tombstone must not retain a usable pinned credential.
        Throws<SqliteException>(
            () => Execute(connection, "UPDATE TrustedManagers SET State='Revoked' WHERE PeerHostId='peer-A';"),
            "revocation must clear the pinned fingerprint");

        Execute(connection, "UPDATE TrustedManagers SET State='Revoked', CurrentTrustedPublicKeyFingerprint=NULL, RevokedUtc='2026-01-02T00:00:00Z' WHERE PeerHostId='peer-A';");
        Equal(1L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM TrustedManagers WHERE PeerHostId='peer-A';"), "tombstone row retained, never deleted");
        return Task.CompletedTask;
    }

    public static Task TestPendingCredentialReplacementCarriesNoGrantAuthority()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        Execute(connection, "INSERT INTO TrustedManagers (PeerHostId, State, CurrentTrustedPublicKeyFingerprint, CreatedUtc) VALUES ('peer-A', 'Active', 'fp-1', '2026-01-01T00:00:00Z');");
        Execute(connection, "INSERT INTO PendingCredentialReplacements (ReplacementId, PeerHostId, ProposedKeyFingerprint, VerifiedUtc, ExpectedTrustState, ExpectedCurrentTrustedPublicKeyFingerprint, ExpiresUtc, CreatedUtc) VALUES ('r1', 'peer-A', 'fp-2', '2026-01-01T00:00:00Z', 'Active', 'fp-1', '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');");

        // PAIR-004: this is a zero-authority concept. Assert the table has no grant linkage at
        // all, so nothing here can silently restore or manufacture authority.
        var grantish = HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM pragma_table_info('PendingCredentialReplacements') WHERE name LIKE '%Grant%' OR name LIKE '%Capability%';");
        Equal(0L, grantish, "PendingCredentialReplacements carries no grant/capability linkage");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- operations

    public static Task TestOperationRecordRequiresExplicitDiscriminatedTarget()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();

        // OPS-004: a HostTarget carries no server, and a ServerTarget is Host-qualified.
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO OperationRecords (OperationId, Kind, TargetKind, TargetHostId, TargetServerProfileId, Phase, StartedUtc) VALUES ('o1', 'ManageHostUpdates', 'HostTarget', 'host-A', 's1', 'Started', '2026-01-01T00:00:00Z');"),
            "HostTarget must not carry a server profile");
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO OperationRecords (OperationId, Kind, TargetKind, TargetHostId, Phase, StartedUtc) VALUES ('o2', 'Backup', 'ServerTarget', 'host-A', 'Started', '2026-01-01T00:00:00Z');"),
            "ServerTarget requires a server profile");

        Execute(connection, "INSERT INTO OperationRecords (OperationId, Kind, TargetKind, TargetHostId, Phase, StartedUtc) VALUES ('o1', 'ManageHostUpdates', 'HostTarget', 'host-A', 'Started', '2026-01-01T00:00:00Z');");
        Execute(connection, "INSERT INTO OperationRecords (OperationId, Kind, TargetKind, TargetHostId, TargetServerProfileId, Phase, StartedUtc) VALUES ('o2', 'Backup', 'ServerTarget', 'host-A', 's1', 'Started', '2026-01-01T00:00:00Z');");
        Equal(2L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM OperationRecords;"), "both target shapes persist");
        return Task.CompletedTask;
    }

    public static Task TestOperationLockScopeIsIndependentOfTargetAndRequiresOwningRecord()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();

        // SS9: a lock can never exist without the OperationRecord startup recovery needs in
        // order to classify it - the accepted atomic record+lock relationship, enforced by FK.
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO OperationLocks (OperationLockId, ScopeKind, ScopeHostId, OperationKind, OwningOperationId, AcquiredUtc) VALUES ('l1', 'HostScope', 'host-A', 'ManageHostUpdates', 'no-such-op', '2026-01-01T00:00:00Z');"),
            "an orphaned lock must be rejected");

        // A SERVER-targeted operation legitimately holding a HOST-scoped lock: target and scope
        // are deliberately separate concepts, not two views of one field.
        Execute(connection, "INSERT INTO OperationRecords (OperationId, Kind, TargetKind, TargetHostId, TargetServerProfileId, Phase, StartedUtc) VALUES ('op1', 'RebuildAll', 'ServerTarget', 'host-A', 's1', 'Started', '2026-01-01T00:00:00Z');");
        Execute(connection, "INSERT INTO OperationLocks (OperationLockId, ScopeKind, ScopeHostId, OperationKind, OwningOperationId, AcquiredUtc) VALUES ('l1', 'HostScope', 'host-A', 'RebuildAll', 'op1', '2026-01-01T00:00:00Z');");

        Equal("ServerTarget", HostDatabase.QueryScalarText(connection, "SELECT TargetKind FROM OperationRecords WHERE OperationId='op1';"), "target stays ServerTarget");
        Equal("HostScope", HostDatabase.QueryScalarText(connection, "SELECT ScopeKind FROM OperationLocks WHERE OperationLockId='l1';"), "scope is independently HostScope");

        // HostScope shape must not carry a server profile.
        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO OperationLocks (OperationLockId, ScopeKind, ScopeHostId, ScopeServerProfileId, OperationKind, OwningOperationId, AcquiredUtc) VALUES ('l2', 'HostScope', 'host-A', 's1', 'X', 'op1', '2026-01-01T00:00:00Z');"),
            "HostScope must not carry a server profile");
        return Task.CompletedTask;
    }

    public static Task TestRecoveryDispositionIsPersistable()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        Execute(connection, "INSERT INTO OperationRecords (OperationId, Kind, TargetKind, TargetHostId, Phase, RecoveryDisposition, StartedUtc) VALUES ('o1', 'ManageHostUpdates', 'HostTarget', 'host-A', 'Applying', 'RequiresManualReview', '2026-01-01T00:00:00Z');");
        Equal("RequiresManualReview", HostDatabase.QueryScalarText(connection, "SELECT RecoveryDisposition FROM OperationRecords WHERE OperationId='o1';"), "disposition persisted");

        Throws<SqliteException>(
            () => Execute(connection, "INSERT INTO OperationRecords (OperationId, Kind, TargetKind, TargetHostId, Phase, RecoveryDisposition, StartedUtc) VALUES ('o2', 'X', 'HostTarget', 'host-A', 'P', 'MadeUpDisposition', '2026-01-01T00:00:00Z');"),
            "only the four accepted dispositions are valid");
        return Task.CompletedTask;
    }

    public static Task TestConfigurationRevisionsSupportRevisionTokens()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        Execute(connection, "INSERT INTO ConfigurationRevisions (ResourceKind, ResourceId, RevisionId, LastModifiedUtc) VALUES ('ServerSettings', 's1', 1, '2026-01-01T00:00:00Z');");
        Execute(connection, "UPDATE ConfigurationRevisions SET RevisionId = RevisionId + 1, LastModifiedUtc='2026-01-02T00:00:00Z' WHERE ResourceKind='ServerSettings' AND ResourceId='s1';");
        Equal(2L, HostDatabase.QueryScalarLong(connection, "SELECT RevisionId FROM ConfigurationRevisions WHERE ResourceId='s1';"), "monotonic revision token");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- audit

    public static Task TestAuditEventsSupportSameTransactionOfflineRecoveryWrites()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        var repo = new HostIdentityRepository(temp.Database);
        repo.EnsureHostIdentity(connection);

        // SS5a: an offline-recovery write and its audit record commit together.
        using (var transaction = connection.BeginTransaction())
        {
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE HostIdentity SET CurrentCredentialRef='new-ref' WHERE Id=1;";
                update.ExecuteNonQuery();
            }

            using (var audit = connection.CreateCommand())
            {
                audit.Transaction = transaction;
                audit.CommandText = "INSERT INTO AuditEvents (AuditEventId, OccurredUtc, EventKind, ActorKind, IsOfflineRecovery, Summary) VALUES ('a1', '2026-01-01T00:00:00Z', 'HostCredentialRecovered', 'OfflineRecovery', 1, 'offline recovery');";
                audit.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        Equal(1L, HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM AuditEvents WHERE IsOfflineRecovery=1;"), "offline-recovery audit event recorded");
        Equal("new-ref", HostDatabase.QueryScalarText(connection, "SELECT CurrentCredentialRef FROM HostIdentity WHERE Id=1;"), "change and audit committed together");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- WAL-safe snapshot

    public static Task TestSnapshotCapturesUncheckpointedWalDataAndPassesIntegrityCheck()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();
        var repo = new HostIdentityRepository(temp.Database);
        var identity = repo.EnsureHostIdentity(connection);

        // Commit a sentinel row and deliberately do NOT checkpoint - it now lives in the WAL.
        Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('wal-sentinel', 'S-1-5-21-WAL', 'k', 0, 'Active', '2026-01-01T00:00:00Z');");
        True(File.Exists(temp.Root.DatabasePath + "-wal"), "WAL file exists (data is uncheckpointed)");

        var snapshot = new HostStateSnapshot(temp.Database);
        var snapshotPath = snapshot.CaptureTo(connection, snapshot.DefaultSnapshotPath("snapshot.db"));

        // Integrity alone is NOT sufficient - a raw .db copy also returns "ok" while missing the
        // committed row. Data presence is the assertion that actually proves correctness.
        True(HostStateSnapshot.VerifyIntegrity(snapshotPath), "snapshot passes PRAGMA integrity_check");

        using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshotPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        verify.Open();
        Equal(1L, HostDatabase.QueryScalarLong(verify, "SELECT COUNT(*) FROM LocalPrincipals WHERE LocalPrincipalId='wal-sentinel';"), "uncheckpointed committed row present in snapshot");
        Equal(identity.HostId, HostDatabase.QueryScalarText(verify, "SELECT HostId FROM HostIdentity WHERE Id=1;"), "HostIdentity present in snapshot");
        return Task.CompletedTask;
    }

    public static Task TestRawFileCopyUnderWalIsUnsafeAndIsNotUsed()
    {
        using var temp = new TempRoot();
        using var connection = temp.OpenMigrated();

        Execute(connection, "INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc) VALUES ('wal-sentinel', 'S-1-5-21-WAL', 'k', 0, 'Active', '2026-01-01T00:00:00Z');");

        // Demonstrates WHY BackupDatabase is required: a raw copy of the .db file alone loses
        // committed-but-uncheckpointed data while still reporting integrity_check = ok.
        var rawCopy = Path.Combine(temp.Directory, "raw-copy.db");
        File.Copy(temp.Root.DatabasePath, rawCopy);

        using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = rawCopy, Mode = SqliteOpenMode.ReadOnly }.ToString());
        verify.Open();
        var rawRows = HostDatabase.QueryScalarLong(verify, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='LocalPrincipals';");

        // The raw copy is missing the schema/data entirely - proving the hazard is real, and
        // that HostStateSnapshot's use of BackupDatabase is not a stylistic preference.
        True(rawRows == 0, "raw .db copy under WAL is demonstrably incomplete");
        return Task.CompletedTask;
    }
}
