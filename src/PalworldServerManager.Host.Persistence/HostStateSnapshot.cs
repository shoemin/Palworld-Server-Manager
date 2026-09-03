using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence;

// SS6's consistent-snapshot requirement.
//
// Under WAL, a live database's true state spans host.db plus -wal/-shm; naively copying the
// .db file alone can produce a snapshot that passes PRAGMA integrity_check while SILENTLY
// MISSING committed data still in the WAL. This uses SQLite's own online backup API instead,
// never a raw filesystem copy.
//
// Host-state maintenance/testing infrastructure only - no server-backup UX, no retention policy.
public sealed class HostStateSnapshot
{
    private readonly HostDatabase _database;

    public HostStateSnapshot(HostDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    // Takes a consistent snapshot of the live database to destinationPath, while it remains open
    // and writable. Returns the destination path.
    public string CaptureTo(SqliteConnection liveConnection, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(liveConnection);
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("A snapshot destination path is required.", nameof(destinationPath));
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        destination.Open();

        liveConnection.BackupDatabase(destination);

        return destinationPath;
    }

    public string CaptureTo(SqliteConnection liveConnection, string destinationDirectory, string fileName)
        => CaptureTo(liveConnection, Path.Combine(destinationDirectory, fileName));

    public string DefaultSnapshotPath(string fileName)
        => Path.Combine(_database.Root.SnapshotsDirectory, fileName);

    // Verifies a snapshot is structurally sound. NOTE: integrity_check alone is NOT sufficient
    // to prove a snapshot is correct - a raw .db copy under WAL returns "ok" while missing
    // committed rows. Callers must also assert expected data presence.
    public static bool VerifyIntegrity(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();

        return string.Equals(HostDatabase.QueryScalarText(connection, "PRAGMA integrity_check;"), "ok", StringComparison.Ordinal);
    }
}
