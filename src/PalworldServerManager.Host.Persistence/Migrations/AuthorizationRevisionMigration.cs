using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

internal sealed class Migration010AuthorizationRevision : IHostSchemaMigration
{
    public int Version => 10;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        HostDatabase.Execute(connection, """
            CREATE TABLE AuthorizationRevision (
                Id INTEGER PRIMARY KEY CHECK(Id=1),
                Revision INTEGER NOT NULL CHECK(typeof(Revision)='integer' AND Revision>=0)
            );
            INSERT INTO AuthorizationRevision VALUES (1,0);
            """, transaction);
        // Includes previous enrollment/recovery/trust writers, not only this repository.
        foreach(var table in new[]{"HostCapabilityGrants","ServerCapabilityGrants","LocalPrincipals","TrustedManagers","HostIdentity"})
        foreach(var action in new[]{"INSERT","UPDATE","DELETE"})
            HostDatabase.Execute(connection, $"""
                CREATE TRIGGER {table}_AuthorizationRevision_{action} AFTER {action} ON {table}
                BEGIN
                    SELECT CASE WHEN
                        (SELECT COUNT(*) FROM AuthorizationRevision WHERE Id=1 AND typeof(Revision)='integer' AND Revision>=0 AND Revision<9223372036854775807)<>1
                        THEN RAISE(ABORT,'Authorization revision unavailable or exhausted.') END;
                    UPDATE AuthorizationRevision SET Revision=Revision+1 WHERE Id=1;
                END;
                """, transaction);
    }
}
