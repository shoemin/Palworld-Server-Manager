using System.Globalization;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Operations;

namespace PalworldServerManager.Host.Persistence;

public sealed class StaleWriteConflictException() : Exception("The resource changed; refresh before retrying.");
public sealed record ConfigurationRevision(EditableResource Resource, long Revision, DateTimeOffset? LastModifiedUtc);

// Trusted Host-only concurrency seam; callers retain the machine exclusivity lease. This is
// NOT authentication/authorization or an RPC. The trusted writer must authorize the actual
// database action here and return a read-only final validator for its current proof, effects
// and audit. External filesystem/process effects cannot be rolled back by this transaction.
public sealed class ConfigurationRevisionRepository
{
    private readonly HostDatabase database;
    private readonly Guid hostId;
    private readonly TimeProvider time;

    public ConfigurationRevisionRepository(HostDatabase database, Guid hostId, TimeProvider? timeProvider = null)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        this.hostId = hostId != Guid.Empty ? hostId : throw new ArgumentException("Host identity required.");
        time = timeProvider ?? TimeProvider.System;
    }

    public ConfigurationRevision Read(EditableResource resource, CancellationToken ct = default)
    {
        RequireResource(resource); ct.ThrowIfCancellationRequested();
        using var c = Open(true); using var tx = c.BeginTransaction(deferred: true);
        RequireHost(c, tx); var result = Read(c, tx, resource); ct.ThrowIfCancellationRequested(); return result;
    }

    public ConfigurationRevision Write(EditableResource resource, long expectedRevision,
        Func<SqliteConnection, SqliteTransaction, Action> writeAndValidate, CancellationToken ct = default)
    {
        RequireResource(resource); ArgumentNullException.ThrowIfNull(writeAndValidate); ct.ThrowIfCancellationRequested();
        if (expectedRevision < 0) throw new StaleWriteConflictException();
        using var c = Open(false); using var tx = c.BeginTransaction(deferred: false);
        RequireHost(c, tx); var before = Read(c, tx, resource);
        if (before.Revision != expectedRevision) throw new StaleWriteConflictException();
        var next = checked(before.Revision + 1); var now = time.GetUtcNow();
        if (now.Offset != TimeSpan.Zero) throw new InvalidOperationException("UTC time required.");
        ct.ThrowIfCancellationRequested();
        // The action and revision belong to this one transaction. No automatic retry and no
        // catch-and-commit path can retain a partial action after an error or stale conflict.
        var validate = writeAndValidate(c, tx) ?? throw new InvalidOperationException("A final effect validator is required.");
        // A trusted action must not advance/reset this token itself, even to a valid value.
        if (Read(c, tx, resource) != before) throw new InvalidOperationException("Resource revision changed during the action.");
        using (var cmd = Command(c, tx, """
            INSERT INTO ConfigurationRevisions (ResourceKind,ResourceId,RevisionId,LastModifiedUtc)
                VALUES ($kind,$id,$next,$now)
            ON CONFLICT (ResourceKind,ResourceId) DO UPDATE SET RevisionId=$next,LastModifiedUtc=$now
                WHERE RevisionId=$expected;
            """, ("$kind", resource.Kind), ("$id", StorageId(resource)), ("$next", next),
            ("$now", now.ToString("O", CultureInfo.InvariantCulture)), ("$expected", before.Revision)))
        {
            if (cmd.ExecuteNonQuery() != 1) throw new StaleWriteConflictException();
        }
        validate();
        RequireHost(c, tx);
        var after = Read(c, tx, resource);
        if (after != new ConfigurationRevision(resource, next, now))
            throw new InvalidOperationException("Resource revision changed before commit.");
        ct.ThrowIfCancellationRequested(); tx.Commit(); return after;
    }

    private void RequireResource(EditableResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        OperationLockPolicy.RequireAuthoritativeTarget(hostId, resource.Target);
    }

    private void RequireHost(SqliteConnection c, SqliteTransaction tx)
    {
        using var cmd = Command(c, tx, """
            SELECT COUNT(*) FROM HostIdentity WHERE Id=1 AND HostId=$host AND HostBootstrapState='Initialized'
                AND (SELECT COUNT(*) FROM LocalPrincipals WHERE IsOwner=1 AND State='Active')=1;
            """, ("$host", hostId.ToString("D")));
        if (Convert.ToInt32(cmd.ExecuteScalar()) != 1) throw new InvalidOperationException("Current initialized Host required.");
    }

    private static ConfigurationRevision Read(SqliteConnection c, SqliteTransaction tx, EditableResource resource)
    {
        using var cmd = Command(c, tx, """
            SELECT RevisionId,LastModifiedUtc FROM ConfigurationRevisions WHERE ResourceKind=$kind AND ResourceId=$id;
            """, ("$kind", resource.Kind), ("$id", StorageId(resource)));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return new(resource, 0, null);
        if (reader.GetValue(0) is not long revision || revision < 0 ||
            reader.GetValue(1) is not string stamp || !DateTimeOffset.TryParseExact(stamp, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var utc) || utc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Invalid persisted resource revision.");
        return new(resource, revision, utc);
    }

    private static string StorageId(EditableResource resource) => resource.Target switch
    {
        HostTarget h => "H:" + h.AuthoritativeHostId.ToString("D"),
        ServerTarget s => "S:" + s.Server.AuthoritativeHostId.ToString("D") + ":" + s.Server.ServerProfileId.ToString("D"),
        _ => throw new ArgumentException("Unknown resource target.")
    };

    private SqliteConnection Open(bool readOnly)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = true }.ToString());
        try { c.Open(); return c; } catch { c.Dispose(); throw; }
    }

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] values)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
}
