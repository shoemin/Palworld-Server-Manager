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
                    (ReplacementId, PeerHostId, ProposedKeyFingerprint, ExpiresUtc, CreatedUtc)
                VALUES ('r1', 'no-such-peer', 'fp', '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
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

    public static Task TestPriorVersionFixtureMigratesToLatest()
    {
        using var temp = new TempRoot();
        using var connection = temp.Database.OpenConnection();
        var runner = HostSchemaMigrationRunner.Default();

        // A "prior version" fixture: a database deliberately left at version 0 with the
        // migration chain not yet applied, which is exactly what an older install looks like.
        Equal(0, HostSchemaMigrationRunner.ReadSchemaVersion(connection), "fixture starts at 0");
        var applied = runner.Migrate(connection);
        Equal(runner.LatestVersion, HostSchemaMigrationRunner.ReadSchemaVersion(connection), "fixture reaches latest");
        Equal(runner.LatestVersion, applied, "applied count equals the gap that existed");
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

        repo.InitializeWithOwner(connection, "owner-1", "S-1-5-21-OWNER", "owner-public-key");

        var identity = repo.TryReadHostIdentity(connection)!;
        Equal(HostBootstrapState.Initialized, identity.BootstrapState, "Host became Initialized");
        Equal(1, HostIdentityRepository.CountActiveOwners(connection), "exactly one active Owner");

        // Never re-initialized, never reverts (SS2c).
        Throws<InvalidOperationException>(
            () => repo.InitializeWithOwner(connection, "owner-2", "S-1-5-21-OTHER", "other-key"),
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
            () => repo.InitializeWithOwner(connection, "owner-x", "S-1-5-21-X", "kx"),
            "a conflicting active Owner aborts the transition");

        // The Host must still be Uninitialized - no committed Initialized-without-exactly-one-Owner
        // state was ever observable.
        Equal(HostBootstrapState.Uninitialized, repo.TryReadHostIdentity(connection)!.BootstrapState, "transition rolled back");
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
        Execute(connection, "INSERT INTO PendingCredentialReplacements (ReplacementId, PeerHostId, ProposedKeyFingerprint, ExpiresUtc, CreatedUtc) VALUES ('r1', 'peer-A', 'fp-2', '2030-01-01T00:00:00Z', '2026-01-01T00:00:00Z');");

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
