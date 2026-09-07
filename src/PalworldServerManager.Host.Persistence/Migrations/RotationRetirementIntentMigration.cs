using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

internal sealed class Migration009RotationRetirementIntent : IHostSchemaMigration
{
    public int Version => 9;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction) => HostDatabase.Execute(connection, """
        ALTER TABLE HostCredentialRotations ADD COLUMN RetirementAuthorized INTEGER NOT NULL DEFAULT 0
            CHECK(RetirementAuthorized IN (0,1));
        -- Earlier completion meant deletion eligibility. Preserve that committed authorization,
        -- but never infer that Old was actually deleted. Existing audit history stays unchanged.
        UPDATE HostCredentialRotations SET State='CutOver',RetirementAuthorized=1,CompletedUtc=NULL
            WHERE State='Completed' AND OldCredentialRef IN
                (SELECT CredentialRef FROM SecureCredentialReferences WHERE RetiredUtc IS NULL);
        """, transaction);
}
