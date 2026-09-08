using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

internal sealed class Migration014ReplacementBindingEvidence : IHostSchemaMigration
{
    public int Version=>14;
    public void Apply(SqliteConnection connection,SqliteTransaction transaction)
    {
        HostDatabase.Execute(connection,"""
            CREATE TABLE PeerReplacementBindingEvidence (
                ReplacementId TEXT NOT NULL PRIMARY KEY REFERENCES PendingCredentialReplacements(ReplacementId) ON DELETE CASCADE,
                SourceIncarnation INTEGER NOT NULL CHECK(typeof(SourceIncarnation)='integer' AND SourceIncarnation>0),
                LocalFingerprint TEXT NOT NULL CHECK(length(LocalFingerprint)=64 AND LocalFingerprint NOT GLOB '*[^0-9A-F]*'),
                VerifiedUtc TEXT NOT NULL
            );
            -- No historical request can acquire fresh verified context through upgrade.
            UPDATE PendingCredentialReplacements SET InvalidatedUtc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE InvalidatedUtc IS NULL AND ApprovedUtc IS NULL;
            CREATE TRIGGER ReplacementCandidate_TrustTransition AFTER UPDATE ON TrustedManagers
            WHEN OLD.State IS NOT NEW.State OR OLD.CurrentTrustedPublicKeyFingerprint IS NOT NEW.CurrentTrustedPublicKeyFingerprint
                OR OLD.PendingTrustedPublicKeyFingerprint IS NOT NEW.PendingTrustedPublicKeyFingerprint
                OR OLD.PendingRotationId IS NOT NEW.PendingRotationId OR OLD.PeerRecoveryRequired IS NOT NEW.PeerRecoveryRequired
                OR OLD.PendingReconfirmationRequired IS NOT NEW.PendingReconfirmationRequired
                OR OLD.CreatedUtc IS NOT NEW.CreatedUtc OR OLD.PairedUtc IS NOT NEW.PairedUtc OR OLD.RevokedUtc IS NOT NEW.RevokedUtc
            BEGIN
                UPDATE PendingCredentialReplacements SET InvalidatedUtc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                    WHERE PeerHostId=NEW.PeerHostId AND InvalidatedUtc IS NULL AND ApprovedUtc IS NULL;
            END;
            CREATE TRIGGER ReplacementCandidate_IncarnationRetired BEFORE DELETE ON PeerRelationshipIncarnations
            BEGIN
                UPDATE PendingCredentialReplacements SET InvalidatedUtc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                    WHERE PeerHostId=OLD.PeerHostId AND InvalidatedUtc IS NULL AND ApprovedUtc IS NULL;
            END;
            CREATE TRIGGER ReplacementCandidate_LocalIdentity AFTER UPDATE ON HostIdentity
            WHEN OLD.HostId IS NOT NEW.HostId OR OLD.HostBootstrapState IS NOT NEW.HostBootstrapState
                OR OLD.CurrentCredentialRef IS NOT NEW.CurrentCredentialRef
            BEGIN
                UPDATE PendingCredentialReplacements SET InvalidatedUtc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                    WHERE InvalidatedUtc IS NULL AND ApprovedUtc IS NULL;
            END;
            CREATE TRIGGER ReplacementCandidate_LocalCredential AFTER UPDATE ON SecureCredentialReferences
            WHEN (OLD.CredentialRef IN (SELECT CurrentCredentialRef FROM HostIdentity) OR NEW.CredentialRef IN (SELECT CurrentCredentialRef FROM HostIdentity))
                AND (OLD.CredentialRef IS NOT NEW.CredentialRef OR OLD.PublicKeyFingerprint IS NOT NEW.PublicKeyFingerprint
                    OR OLD.Purpose IS NOT NEW.Purpose OR OLD.RetiredUtc IS NOT NEW.RetiredUtc OR OLD.ActivatedUtc IS NOT NEW.ActivatedUtc)
            BEGIN
                UPDATE PendingCredentialReplacements SET InvalidatedUtc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                    WHERE InvalidatedUtc IS NULL AND ApprovedUtc IS NULL;
            END;
            CREATE TRIGGER ReplacementCandidate_LocalCredentialRetired BEFORE DELETE ON SecureCredentialReferences
            WHEN OLD.CredentialRef IN (SELECT CurrentCredentialRef FROM HostIdentity)
            BEGIN
                UPDATE PendingCredentialReplacements SET InvalidatedUtc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                    WHERE InvalidatedUtc IS NULL AND ApprovedUtc IS NULL;
            END;
            CREATE TRIGGER ReplacementCandidate_LocalIdentityRetired BEFORE DELETE ON HostIdentity
            BEGIN
                UPDATE PendingCredentialReplacements SET InvalidatedUtc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                    WHERE InvalidatedUtc IS NULL AND ApprovedUtc IS NULL;
            END;
            """,transaction);
    }
}
