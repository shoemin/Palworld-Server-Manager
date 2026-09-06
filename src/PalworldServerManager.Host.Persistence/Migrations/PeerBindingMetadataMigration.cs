using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

internal sealed class Migration003PeerBindingMetadata : IHostSchemaMigration
{
    public int Version => 3;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction) => HostDatabase.Execute(connection, """
        CREATE TABLE TrustedManagerPairings (
            PeerHostId TEXT PRIMARY KEY REFERENCES TrustedManagers(PeerHostId),
            BoundUtc TEXT NOT NULL,
            ExpiresUtc TEXT NOT NULL,
            LocalBoundPublicKeyFingerprint TEXT NULL CHECK (LocalBoundPublicKeyFingerprint IS NULL OR
                (length(LocalBoundPublicKeyFingerprint)=64 AND LocalBoundPublicKeyFingerprint NOT GLOB '*[^0-9A-F]*'))
        );
        -- Prior public rows do not establish which local credential was verified. Do not infer it.
        -- Their original creation time bounds cleanup; upgrade never refreshes the trust window.
        INSERT INTO TrustedManagerPairings (PeerHostId,BoundUtc,ExpiresUtc,LocalBoundPublicKeyFingerprint)
            SELECT PeerHostId,CreatedUtc,strftime('%Y-%m-%dT%H:%M:%fZ',CreatedUtc,'+30 minutes'),NULL
            FROM TrustedManagers WHERE State='PeerBound';
        """, transaction);
}
