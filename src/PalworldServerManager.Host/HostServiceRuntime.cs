using Microsoft.Data.Sqlite;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;

namespace PalworldServerManager.Host;

public sealed class HostServiceRuntime : IDisposable
{
    private readonly HostExclusivityLock _lease;
    private readonly SqliteConnection _connection;
    private int _disposed;
    private HostServiceRuntime(HostExclusivityLock lease, SqliteConnection connection)
    { _lease = lease; _connection = connection; }
    public static HostServiceRuntime Start(HostDataRoot root, CancellationToken ct, string mutexName = HostExclusivityLock.DefaultMutexName)
    {
        ct.ThrowIfCancellationRequested();
        var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero, mutexName)
            ?? throw new InvalidOperationException("The authoritative Host is already running.");
        SqliteConnection? connection = null;
        try
        {
            connection = new HostDatabase(root).OpenConnection();
            HostSchemaMigrationRunner.Default().Migrate(connection);
            ct.ThrowIfCancellationRequested();
            return new(lease, connection);
        }
        catch
        {
            if (connection is not null) { SqliteConnection.ClearPool(connection); connection.Dispose(); }
            lease.Dispose();
            throw;
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { SqliteConnection.ClearPool(_connection); _connection.Dispose(); }
        finally { _lease.Dispose(); }
    }
}
