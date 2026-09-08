using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

internal sealed class Migration013PeerUnpairReceipt : IHostSchemaMigration
{
    public int Version=>13;
    public void Apply(SqliteConnection connection,SqliteTransaction transaction)
    {
        HostDatabase.Execute(connection,"""
            CREATE TABLE PeerUnpairReceipts (
                PeerHostId TEXT NOT NULL PRIMARY KEY REFERENCES TrustedManagers(PeerHostId),
                SourceIncarnation INTEGER NOT NULL CHECK(typeof(SourceIncarnation)='integer' AND SourceIncarnation>0),
                RevokedIncarnation INTEGER NOT NULL UNIQUE REFERENCES PeerRelationshipIncarnations(Incarnation) ON DELETE CASCADE
                    CHECK(typeof(RevokedIncarnation)='integer' AND RevokedIncarnation>SourceIncarnation),
                PeerFingerprint TEXT NOT NULL CHECK(length(PeerFingerprint)=64 AND PeerFingerprint NOT GLOB '*[^0-9A-F]*'),
                LocalFingerprint TEXT NOT NULL CHECK(length(LocalFingerprint)=64 AND LocalFingerprint NOT GLOB '*[^0-9A-F]*'),
                ReceivedUtc TEXT NOT NULL
            );
            """,transaction);
    }
}
