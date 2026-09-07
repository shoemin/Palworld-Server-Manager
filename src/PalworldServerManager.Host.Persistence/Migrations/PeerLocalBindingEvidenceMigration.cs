using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

internal sealed class Migration008PeerLocalBindingEvidence : IHostSchemaMigration
{
    public int Version => 8;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction) => HostDatabase.Execute(connection, """
        CREATE TABLE PeerLocalBindingEvidence (
            PeerHostId TEXT PRIMARY KEY NOT NULL REFERENCES TrustedManagers(PeerHostId) ON DELETE CASCADE,
            Incarnation INTEGER NOT NULL CHECK(typeof(Incarnation)='integer' AND Incarnation>0),
            LocalFingerprint TEXT NOT NULL,
            BoundUtc TEXT NOT NULL
        );
        -- Historical pairing metadata does not certify the current relationship.
        -- Incarnation deliberately is not a cascading FK: stale proof remains distinguishable.
        """, transaction);
}
