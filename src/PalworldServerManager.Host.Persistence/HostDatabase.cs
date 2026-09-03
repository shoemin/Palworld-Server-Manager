using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence;

// Owns opening connections to the Host's single embedded SQLite database (SS6). Every managed
// connection gets WAL journal mode and foreign-key enforcement applied deterministically -
// SQLite defaults foreign_keys OFF per-connection, so it must be set on each one rather than
// once at creation.
public sealed class HostDatabase
{
    private readonly HostDataRoot _root;

    public HostDatabase(HostDataRoot root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
    }

    public HostDataRoot Root => _root;

    public string DatabasePath => _root.DatabasePath;

    public SqliteConnection OpenConnection()
    {
        _root.EnsureCreated();

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _root.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
        }.ToString());

        connection.Open();

        try
        {
            // WAL is required by SS6's concurrent-reader/single-writer model. It is a persistent
            // database property, so re-applying it on an already-WAL database is a cheap no-op.
            Execute(connection, "PRAGMA journal_mode=WAL;");

            // Per-connection, and off by default - must be set every time or the schema's
            // referential constraints silently do not apply.
            Execute(connection, "PRAGMA foreign_keys=ON;");

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static string QueryScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    public static long QueryScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null || value is DBNull ? 0L : Convert.ToInt64(value);
    }

    public static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        command.ExecuteNonQuery();
    }
}
