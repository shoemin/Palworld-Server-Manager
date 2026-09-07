using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

internal sealed class Migration004RotationProposalMetadata : IHostSchemaMigration
{
    public int Version => 4;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction) => HostDatabase.Execute(connection, """
        CREATE TABLE HostRotationProposals (
            ProposalSequence INTEGER PRIMARY KEY AUTOINCREMENT CHECK(ProposalSequence>0),
            RotationId TEXT NOT NULL UNIQUE REFERENCES HostCredentialRotations(RotationId),
            OldFingerprint TEXT NOT NULL CHECK(length(OldFingerprint)=64 AND OldFingerprint NOT GLOB '*[^0-9A-F]*'),
            NewFingerprint TEXT NOT NULL CHECK(length(NewFingerprint)=64 AND NewFingerprint NOT GLOB '*[^0-9A-F]*'),
            PreparedUtc TEXT NOT NULL,
            CHECK(OldFingerprint<>NewFingerprint)
        );
        CREATE TABLE PeerRotationProposals (
            PeerHostId TEXT NOT NULL REFERENCES TrustedManagers(PeerHostId),
            RotationId TEXT NOT NULL,
            ProposalSequence INTEGER NOT NULL CHECK(ProposalSequence>0),
            OldFingerprint TEXT NOT NULL CHECK(length(OldFingerprint)=64 AND OldFingerprint NOT GLOB '*[^0-9A-F]*'),
            NewFingerprint TEXT NOT NULL CHECK(length(NewFingerprint)=64 AND NewFingerprint NOT GLOB '*[^0-9A-F]*'),
            AcceptedUtc TEXT NOT NULL,
            OriginalExpiresUtc TEXT NOT NULL,
            PRIMARY KEY(PeerHostId,RotationId),
            UNIQUE(PeerHostId,OldFingerprint,ProposalSequence),
            CHECK(OldFingerprint<>NewFingerprint)
        );
        -- No wire acknowledgements, sequence history or grants are inferred on upgrade.
        """, transaction);
}
