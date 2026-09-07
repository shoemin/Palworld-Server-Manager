using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

internal sealed class Migration005PeerTrustRevision : IHostSchemaMigration
{
    public int Version => 5;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        HostDatabase.Execute(connection, """
            CREATE TABLE PeerTrustRevision (
                Id INTEGER PRIMARY KEY CHECK(Id=1),
                Revision INTEGER NOT NULL CHECK(typeof(Revision)='integer' AND Revision>=0)
            );
            INSERT INTO PeerTrustRevision VALUES (1,0);
            """, transaction);
        // Identifiers come only from this fixed migration list. Even a no-op UPDATE
        // invalidates an in-flight collection; safety does not depend on clock precision.
        foreach (var table in new[] { "TrustedManagers", "TrustedManagerPairings" })
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
            HostDatabase.Execute(connection, $"""
                CREATE TRIGGER {table}_Revision_{action} AFTER {action} ON {table}
                BEGIN
                    SELECT CASE WHEN
                        (SELECT COUNT(*) FROM PeerTrustRevision WHERE Id=1 AND typeof(Revision)='integer' AND Revision>=0 AND Revision<9223372036854775807)<>1
                        THEN RAISE(ABORT,'Peer trust revision unavailable or exhausted.') END;
                    UPDATE PeerTrustRevision SET Revision=Revision+1 WHERE Id=1;
                END;
                """, transaction);
    }
}
