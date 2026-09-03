using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

public sealed class UnknownSchemaVersionException : Exception
{
    public UnknownSchemaVersionException(int found, int latestKnown)
        : base($"Database schema version {found} is newer than the latest version this build knows ({latestKnown}). Refusing to open - a newer Manager wrote this database, and downgrading it is never attempted.")
    {
        FoundVersion = found;
        LatestKnownVersion = latestKnown;
    }

    public int FoundVersion { get; }

    public int LatestKnownVersion { get; }
}

// Deterministic schema migration using PRAGMA user_version as the sole schema-version
// representation (no bootstrap chicken-and-egg version table).
//
// Atomicity rule (SS3 of #40's contract): each individual migration AND its user_version bump
// commit together in exactly one transaction. If migration N throws, N is fully rolled back and
// user_version remains at the last successfully committed version - never a partially-applied N.
public sealed class HostSchemaMigrationRunner
{
    private readonly IReadOnlyList<IHostSchemaMigration> _migrations;

    public HostSchemaMigrationRunner(IEnumerable<IHostSchemaMigration> migrations)
    {
        _migrations = migrations.OrderBy(m => m.Version).ToList();

        for (var i = 0; i < _migrations.Count; i++)
        {
            var expected = i + 1;
            if (_migrations[i].Version != expected)
            {
                throw new InvalidOperationException($"Schema migrations must be contiguous starting at 1; expected version {expected} but found {_migrations[i].Version}.");
            }
        }
    }

    public static HostSchemaMigrationRunner Default() => new(HostSchema.AllMigrations());

    public int LatestVersion => _migrations.Count == 0 ? 0 : _migrations[^1].Version;

    public static int ReadSchemaVersion(SqliteConnection connection)
        => (int)HostDatabase.QueryScalarLong(connection, "PRAGMA user_version;");

    // Returns the number of migrations actually applied (0 when already current).
    public int Migrate(SqliteConnection connection)
    {
        var current = ReadSchemaVersion(connection);

        if (current > LatestVersion)
        {
            throw new UnknownSchemaVersionException(current, LatestVersion);
        }

        var applied = 0;
        foreach (var migration in _migrations.Where(m => m.Version > current))
        {
            using var transaction = connection.BeginTransaction();
            migration.Apply(connection, transaction);

            // PRAGMA user_version does not accept a parameter binding, and the value is an int
            // from a controlled contiguous sequence - not external input.
            HostDatabase.Execute(connection, $"PRAGMA user_version={migration.Version};", transaction);

            transaction.Commit();
            applied++;
        }

        return applied;
    }
}
