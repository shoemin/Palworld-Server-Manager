using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence;

public enum HostBootstrapState
{
    Uninitialized,
    Initialized,
}

public sealed record HostIdentityRecord(string HostId, HostBootstrapState BootstrapState, string? CurrentCredentialRef, string SetupUtc);

// SS4a/SS2c persistence for the machine's own identity. Bounded to persistence semantics: this
// creates and reads the HostIdentity row and performs the Uninitialized -> Initialized
// transition transactionally. It does NOT implement the Owner bootstrap ceremony (SS2c), which
// requires the privileged offline Host.Cli path plus an online completion phase - that is #42.
public sealed class HostIdentityRepository
{
    private readonly HostDatabase _database;

    public HostIdentityRepository(HostDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    // Generates HostId exactly once. Calling this again on an already-initialized database
    // returns the existing identity unchanged - HostId never changes for this physical PC's
    // life (IDENT-001).
    public HostIdentityRecord EnsureHostIdentity(SqliteConnection connection, Func<string>? hostIdFactory = null)
    {
        var existing = TryReadHostIdentity(connection);
        if (existing is not null)
        {
            return existing;
        }

        var hostId = (hostIdFactory ?? (() => Guid.NewGuid().ToString("D")))();
        var setupUtc = DateTimeOffset.UtcNow.ToString("O");

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO HostIdentity (Id, HostId, HostBootstrapState, CurrentCredentialRef, SetupUtc)
            VALUES (1, $hostId, 'Uninitialized', NULL, $setupUtc);
            """;
        command.Parameters.AddWithValue("$hostId", hostId);
        command.Parameters.AddWithValue("$setupUtc", setupUtc);
        command.ExecuteNonQuery();

        return new HostIdentityRecord(hostId, HostBootstrapState.Uninitialized, null, setupUtc);
    }

    public HostIdentityRecord? TryReadHostIdentity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT HostId, HostBootstrapState, CurrentCredentialRef, SetupUtc FROM HostIdentity WHERE Id = 1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new HostIdentityRecord(
            reader.GetString(0),
            Enum.Parse<HostBootstrapState>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3));
    }

    public static int CountActiveOwners(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM LocalPrincipals WHERE IsOwner = 1 AND State = 'Active';";
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    // LOCAL-001 / OWNER-001, the persistence half.
    //
    // The partial unique index proves AT MOST one active Owner. This method supplies the other
    // half transactionally: the Uninitialized -> Initialized transition commits ONLY together
    // with exactly one active Owner existing. There is therefore no observable committed state
    // with Initialized + zero active Owners, or Initialized + multiple.
    //
    // This is deliberately NOT the SS2c bootstrap ceremony (no OwnerBootstrapSecret, no
    // verifier check, no privileged offline gate, no client keypair exchange) - only the
    // persistence transaction shape that ceremony will later rely on (#42).
    public void InitializeWithOwner(
        SqliteConnection connection,
        string ownerLocalPrincipalId,
        string ownerOsPrincipalRef,
        string ownerPublicVerificationKey)
    {
        using var transaction = connection.BeginTransaction();

        var identity = TryReadHostIdentity(connection)
            ?? throw new InvalidOperationException("HostIdentity must exist before initialization.");

        if (identity.BootstrapState == HostBootstrapState.Initialized)
        {
            throw new InvalidOperationException("Host is already Initialized; it never reverts and is never re-initialized (SS2c).");
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO LocalPrincipals
                    (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc)
                VALUES ($id, $os, $key, 1, 'Active', $now);
                """;
            insert.Parameters.AddWithValue("$id", ownerLocalPrincipalId);
            insert.Parameters.AddWithValue("$os", ownerOsPrincipalRef);
            insert.Parameters.AddWithValue("$key", ownerPublicVerificationKey);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE HostIdentity SET HostBootstrapState = 'Initialized' WHERE Id = 1;";
            update.ExecuteNonQuery();
        }

        // Enforced inside the same transaction as the transition itself, so a violation can
        // never be observed as committed state.
        var activeOwners = CountActiveOwners(connection, transaction);
        if (activeOwners != 1)
        {
            throw new InvalidOperationException($"Refusing to commit Initialized with {activeOwners} active Owner(s); exactly one is required (LOCAL-001).");
        }

        transaction.Commit();
    }
}
