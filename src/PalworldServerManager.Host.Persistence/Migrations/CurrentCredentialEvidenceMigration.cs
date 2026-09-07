using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

internal sealed class Migration007CurrentCredentialEvidence : IHostSchemaMigration
{
    public int Version => 7;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction) => HostDatabase.Execute(connection, """
        CREATE TABLE HostRotationCurrentCredentialEvidence (
            RotationId TEXT NOT NULL,
            PeerHostId TEXT NOT NULL,
            Incarnation INTEGER NOT NULL CHECK(typeof(Incarnation)='integer' AND Incarnation>0),
            ConfirmedUtc TEXT NOT NULL,
            PRIMARY KEY(RotationId,PeerHostId),
            FOREIGN KEY(RotationId,PeerHostId) REFERENCES HostCredentialRotationPeers(RotationId,PeerHostId) ON DELETE CASCADE
        );
        -- No confirmation is inferred from prior promotion history or current TLS metadata.
        """, transaction);
}
