using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

// Public metadata only. Nullable supports tracked creation before private material exists.
// No fingerprint is inferred from an untrusted publication during upgrade.
internal sealed class Migration002HostCredentialMetadata : IHostSchemaMigration
{
    public int Version => 2;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction) => HostDatabase.Execute(connection, """
        ALTER TABLE SecureCredentialReferences ADD COLUMN PublicKeyFingerprint TEXT NULL
            CHECK (PublicKeyFingerprint IS NULL OR (length(PublicKeyFingerprint)=64 AND PublicKeyFingerprint NOT GLOB '*[^0-9A-F]*'));
        ALTER TABLE SecureCredentialReferences ADD COLUMN ActivatedUtc TEXT NULL;
        UPDATE SecureCredentialReferences SET ActivatedUtc=CreatedUtc
            WHERE CredentialRef IN (SELECT CurrentCredentialRef FROM HostIdentity
                UNION SELECT OldCredentialRef FROM HostCredentialRotations UNION SELECT NewCredentialRef FROM HostCredentialRotations);
        """, transaction);
}
