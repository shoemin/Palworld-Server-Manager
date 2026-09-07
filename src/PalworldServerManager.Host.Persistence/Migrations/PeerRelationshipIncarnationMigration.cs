using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

internal sealed class Migration006PeerRelationshipIncarnation : IHostSchemaMigration
{
    public int Version => 6;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        HostDatabase.Execute(connection, """
            CREATE TABLE PeerRelationshipIncarnations (
                Incarnation INTEGER PRIMARY KEY AUTOINCREMENT CHECK(Incarnation>0),
                PeerHostId TEXT NOT NULL UNIQUE REFERENCES TrustedManagers(PeerHostId) ON DELETE CASCADE
            );
            INSERT INTO PeerRelationshipIncarnations (PeerHostId) SELECT PeerHostId FROM TrustedManagers ORDER BY PeerHostId;
            CREATE TABLE HostRotationPromotionEvidence (
                RotationId TEXT NOT NULL,
                PeerHostId TEXT NOT NULL,
                Incarnation INTEGER NOT NULL CHECK(typeof(Incarnation)='integer' AND Incarnation>0),
                ConfirmedUtc TEXT NOT NULL,
                PRIMARY KEY(RotationId,PeerHostId),
                FOREIGN KEY(RotationId,PeerHostId) REFERENCES HostCredentialRotationPeers(RotationId,PeerHostId) ON DELETE CASCADE
            );
            -- Legacy PromotedUtc remains history, not proof for the newly assigned relationship.
            -- Evidence deliberately has no FK to the current incarnation: stale evidence is retained.
            CREATE TRIGGER TrustedManagers_Incarnation_INSERT AFTER INSERT ON TrustedManagers
            BEGIN
                INSERT INTO PeerRelationshipIncarnations (PeerHostId) VALUES (NEW.PeerHostId);
            END;
            CREATE TRIGGER TrustedManagers_Incarnation_Guard BEFORE UPDATE ON TrustedManagers
            BEGIN
                SELECT CASE WHEN OLD.PeerHostId IS NOT NEW.PeerHostId OR
                    (SELECT COUNT(*) FROM PeerRelationshipIncarnations WHERE PeerHostId=OLD.PeerHostId AND Incarnation>0)<>1
                    THEN RAISE(ABORT,'Peer relationship incarnation unavailable or identity changed.') END;
            END;
            CREATE TRIGGER TrustedManagerPairings_Incarnation_Identity BEFORE UPDATE ON TrustedManagerPairings
            WHEN OLD.PeerHostId IS NOT NEW.PeerHostId
            BEGIN SELECT RAISE(ABORT,'Pairing identity cannot change.'); END;
            CREATE TRIGGER TrustedManagers_Incarnation_UPDATE AFTER UPDATE ON TrustedManagers
            WHEN (OLD.State IS NOT NEW.State AND NOT (OLD.State='PeerBound' AND NEW.State='Active'))
                OR OLD.PeerRecoveryRequired IS NOT NEW.PeerRecoveryRequired
                OR OLD.RevokedUtc IS NOT NEW.RevokedUtc OR OLD.CreatedUtc IS NOT NEW.CreatedUtc
                OR (OLD.PairedUtc IS NOT NEW.PairedUtc AND NOT (OLD.State='PeerBound' AND NEW.State='Active'))
                OR (OLD.CurrentTrustedPublicKeyFingerprint IS NOT NEW.CurrentTrustedPublicKeyFingerprint AND NOT (
                    OLD.State='Active' AND NEW.State='Active' AND OLD.PeerRecoveryRequired=0 AND NEW.PeerRecoveryRequired=0
                    AND OLD.PendingTrustedPublicKeyFingerprint IS NOT NULL
                    AND OLD.PendingTrustedPublicKeyFingerprint IS NOT OLD.CurrentTrustedPublicKeyFingerprint
                    AND NEW.CurrentTrustedPublicKeyFingerprint IS OLD.PendingTrustedPublicKeyFingerprint
                    AND OLD.PendingRotationId IS NOT NULL AND NEW.PendingRotationId IS OLD.PendingRotationId
                    AND OLD.PendingRotationExpiresUtc IS NOT NULL
                    AND NEW.PendingTrustedPublicKeyFingerprint IS NULL AND NEW.PendingRotationExpiresUtc IS NULL
                    AND NEW.PendingReconfirmationRequired=0))
            BEGIN
                DELETE FROM PeerRelationshipIncarnations WHERE PeerHostId=NEW.PeerHostId;
                INSERT INTO PeerRelationshipIncarnations (PeerHostId) VALUES (NEW.PeerHostId);
            END;
            """, transaction);
        // Pairing metadata is proof provenance. Even an identical rewrite invalidates it.
        // Ordinary activation and routine peer-key rotation do not rewrite this table.
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            var row = action == "DELETE" ? "OLD" : "NEW";
            HostDatabase.Execute(connection, $"""
                CREATE TRIGGER TrustedManagerPairings_Incarnation_{action} AFTER {action} ON TrustedManagerPairings
                BEGIN
                    SELECT CASE WHEN
                        (SELECT COUNT(*) FROM PeerRelationshipIncarnations WHERE PeerHostId={row}.PeerHostId AND Incarnation>0)<>1
                        THEN RAISE(ABORT,'Peer relationship incarnation unavailable.') END;
                    DELETE FROM PeerRelationshipIncarnations WHERE PeerHostId={row}.PeerHostId;
                    INSERT INTO PeerRelationshipIncarnations (PeerHostId) VALUES ({row}.PeerHostId);
                END;
                """, transaction);
        }
    }
}
