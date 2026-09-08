using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

internal sealed class Migration016RecoveryCompletionReceipt : IHostSchemaMigration
{
    public int Version=>16;
    public void Apply(SqliteConnection connection,SqliteTransaction transaction)
    {
        HostDatabase.Execute(connection,"""
            CREATE UNIQUE INDEX PeerRelationshipIncarnations_Identity ON PeerRelationshipIncarnations(PeerHostId,Incarnation);
            CREATE TABLE PeerRecoveryCompletionReceipts (
                PeerHostId TEXT NOT NULL PRIMARY KEY REFERENCES TrustedManagers(PeerHostId),
                ApprovalId TEXT NOT NULL,
                SourceIncarnation INTEGER NOT NULL CHECK(typeof(SourceIncarnation)='integer' AND SourceIncarnation>0),
                ResultIncarnation INTEGER NOT NULL CHECK(typeof(ResultIncarnation)='integer' AND ResultIncarnation>=SourceIncarnation),
                PeerFingerprint TEXT NOT NULL CHECK(length(PeerFingerprint)=64 AND PeerFingerprint NOT GLOB '*[^0-9A-F]*'),
                LocalFingerprint TEXT NOT NULL CHECK(length(LocalFingerprint)=64 AND LocalFingerprint NOT GLOB '*[^0-9A-F]*'),
                ReceivedUtc TEXT NOT NULL,
                FOREIGN KEY(PeerHostId,ResultIncarnation) REFERENCES PeerRelationshipIncarnations(PeerHostId,Incarnation) ON DELETE CASCADE
            );
            """,transaction);
    }
}
