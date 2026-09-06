using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence;

public sealed record LocalPrincipalAuthenticationRecord(Guid HostId, Guid LocalPrincipalId, string OsPrincipalRef,
    string PublicVerificationKey, bool IsOwner);

// Read-only online Host seam. Each read owns its connection so concurrent connections do not
// share mutable SqliteConnection state. The Host caller retains its existing machine lease.
public sealed class LocalPrincipalAuthenticationRepository(HostDatabase database)
{
    private readonly HostDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    public LocalPrincipalAuthenticationRecord? TryReadActive(Guid principalId)
    {
        if (principalId == Guid.Empty) return null;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _database.DatabasePath, Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Default, ForeignKeys = true, Pooling = false
        }.ToString());
        connection.Open(); // Never create/recover a database as a side effect of authentication.
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.HostId, p.LocalPrincipalId, p.OsPrincipalRef, p.PublicVerificationKey, p.IsOwner
            FROM LocalPrincipals p CROSS JOIN HostIdentity h
            WHERE h.Id = 1 AND h.HostBootstrapState = 'Initialized'
                AND p.LocalPrincipalId = $id AND p.State = 'Active'
                AND (SELECT COUNT(*) FROM LocalPrincipals WHERE State='Active' AND IsOwner=1) = 1;
            """;
        command.Parameters.AddWithValue("$id", principalId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (!Guid.TryParse(reader.GetString(0), out var hostId) || hostId == Guid.Empty ||
            !Guid.TryParse(reader.GetString(1), out var id) || id == Guid.Empty)
            throw new InvalidDataException("Invalid persisted local identity.");
        return new(hostId, id, reader.GetString(2), reader.GetString(3), reader.GetInt32(4) == 1);
    }
}
