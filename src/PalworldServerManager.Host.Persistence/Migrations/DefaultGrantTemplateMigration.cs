using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

internal sealed class Migration011DefaultGrantTemplate : IHostSchemaMigration
{
    public int Version=>11;
    public void Apply(SqliteConnection connection,SqliteTransaction transaction)
    {
        HostDatabase.Execute(connection,"""
            CREATE TABLE DefaultGrantTemplateState (
                Id INTEGER PRIMARY KEY CHECK(Id=1),
                ConfigurationId TEXT NULL,
                ConfiguredByLocalPrincipalId TEXT NULL REFERENCES LocalPrincipals(LocalPrincipalId),
                ConfiguredUtc TEXT NULL,
                CHECK((ConfigurationId IS NULL AND ConfiguredByLocalPrincipalId IS NULL AND ConfiguredUtc IS NULL)
                    OR (ConfigurationId IS NOT NULL AND ConfiguredByLocalPrincipalId IS NOT NULL AND ConfiguredUtc IS NOT NULL))
            );
            INSERT INTO DefaultGrantTemplateState (Id) VALUES (1);
            CREATE TABLE HostDefaultGrants (
                Capability TEXT PRIMARY KEY,
                CanDelegate INTEGER NOT NULL CHECK(CanDelegate IN (0,1)),
                CanDelegateOnwardDelegation INTEGER NOT NULL CHECK(CanDelegateOnwardDelegation IN (0,1)),
                CHECK(CanDelegate=1 OR CanDelegateOnwardDelegation=0)
            );
            CREATE TABLE ServerDefaultGrants (
                AuthoritativeHostId TEXT NOT NULL, ServerProfileId TEXT NOT NULL, Capability TEXT NOT NULL,
                CanDelegate INTEGER NOT NULL CHECK(CanDelegate IN (0,1)),
                CanDelegateOnwardDelegation INTEGER NOT NULL CHECK(CanDelegateOnwardDelegation IN (0,1)),
                PRIMARY KEY(AuthoritativeHostId,ServerProfileId,Capability),
                CHECK(CanDelegate=1 OR CanDelegateOnwardDelegation=0)
            );
            """,transaction);
        foreach(var table in new[]{"DefaultGrantTemplateState","HostDefaultGrants","ServerDefaultGrants"})
        foreach(var action in new[]{"INSERT","UPDATE","DELETE"})
            HostDatabase.Execute(connection,$"""
                CREATE TRIGGER {table}_AuthorizationRevision_{action} AFTER {action} ON {table}
                BEGIN
                    SELECT CASE WHEN
                        (SELECT COUNT(*) FROM AuthorizationRevision WHERE Id=1 AND typeof(Revision)='integer' AND Revision>=0 AND Revision<9223372036854775807)<>1
                        THEN RAISE(ABORT,'Authorization revision unavailable or exhausted.') END;
                    UPDATE AuthorizationRevision SET Revision=Revision+1 WHERE Id=1;
                END;
                """,transaction);
    }
}
