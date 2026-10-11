using System.Globalization;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;

namespace PalworldServerManager.Host.Persistence;

// Trusted Host data, not a wire DTO, launch configuration or filesystem capability.
// In particular, absence of a row/path says nothing about a tracked process lifetime.
public sealed class ServerInventoryObservation
{
    public ServerRef Server { get; }
    public string DisplayName { get; }
    public string? InstallPath { get; }
    public int GamePort { get; }
    public int RestApiPort { get; }
    public string? ImportProvenance { get; }
    public DateTimeOffset CreatedUtc { get; }

    internal ServerInventoryObservation(ServerRef server, string displayName, string? installPath,
        int gamePort, int restApiPort, string? importProvenance, DateTimeOffset createdUtc)
    {
        Server = server; DisplayName = displayName; InstallPath = installPath;
        GamePort = gamePort; RestApiPort = restApiPort;
        ImportProvenance = importProvenance; CreatedUtc = createdUtc;
    }
}

// The Host owns the exclusivity lease and authenticates/authorizes every consumer.
// This repository neither creates storage nor treats a historical observation as a permit.
public sealed class ServerInventoryRepository
{
    private readonly HostDatabase database;
    private readonly Guid hostId;

    public ServerInventoryRepository(HostDatabase database, Guid hostId)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        this.hostId = hostId != Guid.Empty ? hostId : throw new ArgumentException("Host identity required.");
    }

    public ServerInventoryObservation? Read(ServerRef server, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.AuthoritativeHostId != hostId) throw new ArgumentException("Authoritative local server required.");
        ct.ThrowIfCancellationRequested();
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, ForeignKeys = true
        }.ToString());
        c.Open();
        using var tx = c.BeginTransaction(deferred: true);
        return Read(c, tx, server, ct);
    }

    // A read-only same-transaction check, including pending mutations, never a new writer.
    public void ValidateUnchanged(SqliteConnection c, SqliteTransaction tx, ServerInventoryObservation expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected); database.RequireOwnedTransaction(c, tx);
        var current = Read(c, tx, expected.Server, ct);
        if (current is null || current.InstallPath != expected.InstallPath || current.CreatedUtc != expected.CreatedUtc ||
            current.DisplayName != expected.DisplayName || current.GamePort != expected.GamePort || current.RestApiPort != expected.RestApiPort ||
            current.ImportProvenance != expected.ImportProvenance)
            throw new InvalidOperationException("Registered server changed during settings observation.");
    }

    private ServerInventoryObservation? Read(SqliteConnection c, SqliteTransaction tx, ServerRef server, CancellationToken ct)
    {
        if (server.AuthoritativeHostId != hostId) throw new ArgumentException("Authoritative local server required.");
        ct.ThrowIfCancellationRequested();
        using (var identity = c.CreateCommand())
        {
            identity.Transaction = tx;
            identity.CommandText = """
                SELECT COUNT(*) FROM HostIdentity WHERE Id=1 AND HostId=$host AND HostBootstrapState='Initialized'
                    AND (SELECT COUNT(*) FROM LocalPrincipals WHERE IsOwner=1 AND State='Active')=1;
                """;
            identity.Parameters.AddWithValue("$host", hostId.ToString("D"));
            if (Convert.ToInt32(identity.ExecuteScalar()) != 1)
                throw new InvalidOperationException("Current initialized Host required.");
        }
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT DisplayName,InstallPath,GamePort,RestApiPort,ImportProvenance,CreatedUtc FROM ServerInventory
                WHERE AuthoritativeHostId=$host AND ServerProfileId=$server;
            """;
        cmd.Parameters.AddWithValue("$host", hostId.ToString("D"));
        cmd.Parameters.AddWithValue("$server", server.ServerProfileId.ToString("D"));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) { ct.ThrowIfCancellationRequested(); return null; }
        var name = Text(r, 0, false)!; var path = Text(r, 1, true);
        var game = Port(r, 2); var rest = Port(r, 3); var import = Text(r, 4, true);
        var stamp = Text(r, 5, false)!;
        if (!DateTimeOffset.TryParseExact(stamp, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var utc) ||
            utc.Offset != TimeSpan.Zero || stamp != utc.ToString("O", CultureInfo.InvariantCulture))
            throw InvalidStorage();
        ct.ThrowIfCancellationRequested();
        return new(server, name, path, game, rest, import, utc);
    }

    private static string? Text(SqliteDataReader r, int column, bool nullable)
        => r.GetValue(column) switch { string value => value, DBNull when nullable => null, _ => throw InvalidStorage() };

    // Preserve recorded Int32 values; this does not certify valid/available launch ports.
    private static int Port(SqliteDataReader r, int column)
        => r.GetValue(column) is long value && value is >= int.MinValue and <= int.MaxValue
            ? (int)value : throw InvalidStorage();

    private static InvalidDataException InvalidStorage() => new("Invalid persisted server inventory.");
}
