using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

internal sealed class Migration015ReplacementCompletion : IHostSchemaMigration
{
    public int Version=>15;
    public void Apply(SqliteConnection connection,SqliteTransaction transaction)
    {
        HostDatabase.Execute(connection,"""
            CREATE UNIQUE INDEX ReplacementCandidate_Identity ON PendingCredentialReplacements(ReplacementId,PeerHostId);
            CREATE TABLE PeerReplacementCompletions (
                PeerHostId TEXT NOT NULL PRIMARY KEY REFERENCES TrustedManagers(PeerHostId),
                ReplacementId TEXT NOT NULL UNIQUE,
                SourceIncarnation INTEGER NOT NULL CHECK(typeof(SourceIncarnation)='integer' AND SourceIncarnation>0),
                ApprovalIncarnation INTEGER NOT NULL CHECK(typeof(ApprovalIncarnation)='integer' AND ApprovalIncarnation>SourceIncarnation),
                CurrentIncarnation INTEGER NOT NULL CHECK(typeof(CurrentIncarnation)='integer' AND CurrentIncarnation>=ApprovalIncarnation),
                ApprovedPeerFingerprint TEXT NOT NULL CHECK(length(ApprovedPeerFingerprint)=64 AND ApprovedPeerFingerprint NOT GLOB '*[^0-9A-F]*'),
                LocalFingerprintAtApproval TEXT NOT NULL CHECK(length(LocalFingerprintAtApproval)=64 AND LocalFingerprintAtApproval NOT GLOB '*[^0-9A-F]*'),
                ApprovedUtc TEXT NOT NULL,
                ConfirmedUtc TEXT NULL,
                InvalidatedUtc TEXT NULL,
                FOREIGN KEY(ReplacementId,PeerHostId) REFERENCES PendingCredentialReplacements(ReplacementId,PeerHostId) ON DELETE CASCADE
            );
            -- Security completion only; no address, command payload, authority or backfill.
            CREATE TRIGGER ReplacementCompletion_TrustChanged AFTER UPDATE ON TrustedManagers
            WHEN OLD.State IS NOT NEW.State OR OLD.CurrentTrustedPublicKeyFingerprint IS NOT NEW.CurrentTrustedPublicKeyFingerprint
            BEGIN
                UPDATE PeerReplacementCompletions SET InvalidatedUtc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                    WHERE PeerHostId=NEW.PeerHostId AND InvalidatedUtc IS NULL;
            END;
            """,transaction);
    }
}
