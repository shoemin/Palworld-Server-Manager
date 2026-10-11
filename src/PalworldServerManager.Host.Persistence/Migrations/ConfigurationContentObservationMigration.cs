using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

internal sealed class Migration017ConfigurationContentObservations : IHostSchemaMigration
{
    public int Version => 17;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
        => HostDatabase.Execute(connection, """
            CREATE TABLE ConfigurationContentObservations (
                ResourceKind TEXT NOT NULL,
                ResourceId TEXT NOT NULL,
                ObservedRevision INTEGER NOT NULL CHECK(typeof(ObservedRevision)='integer' AND ObservedRevision>0),
                ContentFingerprint BLOB NOT NULL CHECK(typeof(ContentFingerprint)='blob' AND length(ContentFingerprint)=32),
                PRIMARY KEY(ResourceKind,ResourceId),
                FOREIGN KEY(ResourceKind,ResourceId) REFERENCES ConfigurationRevisions(ResourceKind,ResourceId) ON DELETE CASCADE
            );
            """, transaction);
}
