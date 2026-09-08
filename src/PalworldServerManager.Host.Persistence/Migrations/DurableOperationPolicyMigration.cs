using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

internal sealed class Migration012DurableOperationPolicy : IHostSchemaMigration
{
    public int Version => 12;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        HostDatabase.Execute(connection, """
            ALTER TABLE OperationRecords ADD COLUMN LockRequirement TEXT NULL
                CHECK(LockRequirement IS NULL OR LockRequirement IN ('None','ServerExclusive','HostExclusive'));
            ALTER TABLE OperationRecords ADD COLUMN PolicyFingerprint TEXT NULL
                CHECK(PolicyFingerprint IS NULL OR (length(PolicyFingerprint)=64 AND PolicyFingerprint NOT GLOB '*[^0-9A-F]*'));
            ALTER TABLE OperationRecords ADD COLUMN RecordRevision INTEGER NOT NULL DEFAULT 0
                CHECK(typeof(RecordRevision)='integer' AND RecordRevision>=0)
                CHECK((LockRequirement IS NULL AND PolicyFingerprint IS NULL AND RecordRevision=0)
                    OR (LockRequirement IS NOT NULL AND PolicyFingerprint IS NOT NULL AND RecordRevision>0));
            CREATE TABLE OperationStateRevision (
                Id INTEGER PRIMARY KEY CHECK(Id=1),
                Revision INTEGER NOT NULL CHECK(typeof(Revision)='integer' AND Revision>=0)
            );
            INSERT INTO OperationStateRevision VALUES (1,0);
            """, transaction);
        // Existing rows retain absent qualification metadata; this never blesses old phases.
        foreach (var table in new[] { "OperationRecords", "OperationLocks" })
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
            HostDatabase.Execute(connection, $"""
                CREATE TRIGGER {table}_OperationRevision_{action} AFTER {action} ON {table}
                BEGIN
                    SELECT CASE WHEN
                        (SELECT COUNT(*) FROM OperationStateRevision WHERE Id=1 AND typeof(Revision)='integer'
                            AND Revision>=0 AND Revision<9223372036854775807)<>1
                        THEN RAISE(ABORT,'Operation revision unavailable or exhausted.') END;
                    UPDATE OperationStateRevision SET Revision=Revision+1 WHERE Id=1;
                END;
                """, transaction);
    }
}
