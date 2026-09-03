using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

// The authoritative SS6 schema foundation.
//
// SCOPE RULE (#40): a table existing here does NOT mean the feature that consumes it is
// implemented. This migration establishes storage shapes and the constraints #19 fixes as
// semantic; the runtime engines (pairing, authorization evaluation, operation conflict
// acquisition, recovery classification, Owner ceremonies) belong to later children.
//
// LocalHostTrustDescriptor is deliberately absent - SS6/SS3b require it to be a separate public
// artifact readable by any authorized local user, which the Host-exclusive database
// structurally cannot be (PERSIST-001, HOST-002).
internal sealed class Migration001InitialHostSchema : IHostSchemaMigration
{
    public int Version => 1;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        HostDatabase.Execute(connection, Sql, transaction);
    }

    private const string Sql = """
        -- SS4a: stable semantic HostId, separate from the rotatable authentication credential
        -- (IDENT-001). CurrentCredentialRef is an OPAQUE reference into ISecureCredentialStore
        -- (SS7) - there is deliberately no column here shaped to hold private key bytes.
        -- Singleton enforced structurally: a second row is impossible, not merely never written.
        CREATE TABLE HostIdentity (
            Id                   INTEGER PRIMARY KEY CHECK (Id = 1),
            HostId               TEXT    NOT NULL UNIQUE,
            HostBootstrapState   TEXT    NOT NULL CHECK (HostBootstrapState IN ('Uninitialized','Initialized')),
            CurrentCredentialRef TEXT        NULL,
            SetupUtc             TEXT    NOT NULL
        );

        -- SS3a. One LocalPrincipal per canonical OsPrincipalRef. Revoked rows are RETAINED as
        -- durable tombstones, never deleted (LOCAL-003). Only the public verification key is
        -- stored; the private half is held client-side by that user (LOCAL-002).
        CREATE TABLE LocalPrincipals (
            LocalPrincipalId      TEXT PRIMARY KEY,
            OsPrincipalRef        TEXT NOT NULL UNIQUE,
            PublicVerificationKey TEXT     NULL,
            IsOwner               INTEGER NOT NULL CHECK (IsOwner IN (0,1)),
            State                 TEXT NOT NULL CHECK (State IN ('Active','Revoked')),
            DisplayName           TEXT     NULL,
            CreatedUtc            TEXT NOT NULL,
            RevokedUtc            TEXT     NULL,
            -- SS3a's accepted key/state pairing, both directions: an Active principal HAS a
            -- current PublicVerificationKey, and a Revoked one has it cleared (LOCAL-002,
            -- LOCAL-003). Neither half alone is sufficient - an Active row with no key could
            -- never authenticate, and a Revoked row with a key would still be usable.
            CHECK ((State = 'Active'  AND PublicVerificationKey IS NOT NULL)
                OR (State = 'Revoked' AND PublicVerificationKey IS NULL))
        );

        -- LOCAL-001, OWNER-001: at most one ACTIVE Owner, enforced by the database rather than
        -- by application discipline. This proves "at most one"; the "exactly one once
        -- Initialized" half is enforced transactionally by the initialization path.
        CREATE UNIQUE INDEX UX_LocalPrincipals_SingleActiveOwner
            ON LocalPrincipals (IsOwner)
            WHERE IsOwner = 1 AND State = 'Active';

        -- SS3a. Owner-created, single-use, bounded-expiry enrollment/reactivation authorization.
        -- Stores only a keyed verifier - never the raw EnrollmentCode (SS7). Retained after
        -- consumption with ResultLocalPrincipalId so a lost-response retry is idempotent.
        CREATE TABLE PendingLocalPrincipalEnrollments (
            EnrollmentId           TEXT PRIMARY KEY,
            OsPrincipalRef         TEXT NOT NULL,
            EnrollmentCodeVerifier TEXT NOT NULL,
            -- SS3a's ExistingLocalPrincipalId: set when this reactivates a Revoked principal
            -- rather than creating a new one.
            TargetLocalPrincipalId TEXT     NULL REFERENCES LocalPrincipals(LocalPrincipalId),
            -- SS3a, LOCAL-003: enrollment/reactivation is Owner-CREATED. Persisting which Owner
            -- principal authorized it is part of the accepted shape - OS transport/group
            -- eligibility never authorizes enrollment, so the authorizing actor is recorded.
            CreatedByOwnerLocalPrincipalId TEXT NOT NULL REFERENCES LocalPrincipals(LocalPrincipalId),
            ExpiresUtc             TEXT NOT NULL,
            ConsumedUtc            TEXT     NULL,
            ResultLocalPrincipalId TEXT     NULL REFERENCES LocalPrincipals(LocalPrincipalId),
            FailedAttempts         INTEGER NOT NULL DEFAULT 0,
            InvalidatedUtc         TEXT     NULL,
            CreatedUtc             TEXT NOT NULL
        );

        -- SS2c, OWNER-002. Single OsPrincipalRef-bound initial-Owner bootstrap authorization.
        -- Verifier only, never the raw OwnerBootstrapSecret.
        CREATE TABLE PendingOwnerEnrollments (
            PendingOwnerEnrollmentId TEXT PRIMARY KEY,
            OsPrincipalRef           TEXT NOT NULL,
            SecretVerifier           TEXT NOT NULL,
            ExpiresUtc               TEXT NOT NULL,
            ConsumedUtc              TEXT     NULL,
            ResultLocalPrincipalId   TEXT     NULL REFERENCES LocalPrincipals(LocalPrincipalId),
            InvalidatedUtc           TEXT     NULL,
            CreatedUtc               TEXT NOT NULL
        );

        -- SS2c: at most one UNCONSUMED initial-Owner bootstrap ticket may exist at a time.
        -- The predicate indexes an expression that is identically true for every live row, so a
        -- second live row collides; consumed or explicitly invalidated rows drop out of the
        -- index entirely and are retained as history without blocking a later live ticket
        -- (retention is required for SS2c's idempotent-retry rule).
        CREATE UNIQUE INDEX UX_PendingOwnerEnrollments_SingleLive
            ON PendingOwnerEnrollments ((ConsumedUtc IS NULL AND InvalidatedUtc IS NULL))
            WHERE ConsumedUtc IS NULL AND InvalidatedUtc IS NULL;

        -- SS2b "Rotate the Owner's credential". ExpectedCurrentPublicVerificationKey is the
        -- stale-ticket snapshot check; SS8's proactive invalidation (InvalidatedUtc) is what
        -- actually closes the ABA case.
        CREATE TABLE PendingOwnerCredentialRotations (
            RotationTicketId                   TEXT PRIMARY KEY,
            LocalPrincipalId                   TEXT NOT NULL REFERENCES LocalPrincipals(LocalPrincipalId),
            OsPrincipalRef                     TEXT NOT NULL,
            SecretVerifier                     TEXT NOT NULL,
            -- SS2b: every rotation ticket captures the current active Owner's key at creation -
            -- it is the stale-ticket check itself, so it is never optional.
            ExpectedCurrentPublicVerificationKey TEXT NOT NULL,
            ExpiresUtc                         TEXT NOT NULL,
            ConsumedUtc                        TEXT     NULL,
            InvalidatedUtc                     TEXT     NULL,
            CreatedUtc                         TEXT NOT NULL
        );

        -- SS2b "Re-home Owner status". Captures both the current-Owner side and the target side,
        -- per the accepted staleness checks.
        CREATE TABLE PendingOwnerRehomes (
            RehomeTicketId                        TEXT PRIMARY KEY,
            NewOsPrincipalRef                     TEXT NOT NULL,
            SecretVerifier                        TEXT NOT NULL,
            -- SS2b: a re-home exists only against an initialized Host with an existing active
            -- Owner, so the current-Owner snapshot is never optional.
            ExpectedCurrentOwnerLocalPrincipalId  TEXT NOT NULL REFERENCES LocalPrincipals(LocalPrincipalId),
            ExpectedCurrentOwnerPublicVerificationKey TEXT NOT NULL,
            ExpectedTargetLocalPrincipalId        TEXT     NULL REFERENCES LocalPrincipals(LocalPrincipalId),
            ExpectedTargetState                   TEXT     NULL CHECK (ExpectedTargetState IS NULL OR ExpectedTargetState IN ('Active','Revoked')),
            ExpectedTargetPublicVerificationKey   TEXT     NULL,
            ExpiresUtc                            TEXT NOT NULL,
            ConsumedUtc                           TEXT     NULL,
            ResultLocalPrincipalId                TEXT     NULL REFERENCES LocalPrincipals(LocalPrincipalId),
            InvalidatedUtc                        TEXT     NULL,
            CreatedUtc                            TEXT NOT NULL,
            -- SS2b's two valid target shapes: either no LocalPrincipal existed for the target
            -- account at creation (all three null - the single-user, brand-new-account case), or
            -- one did, in which case its captured key must agree with its captured state exactly
            -- the way LocalPrincipals itself constrains them.
            -- NOTE the explicit "ExpectedTargetState IS NOT NULL": a SQLite CHECK rejects only a
            -- FALSE result, and passes on NULL. Without it, a half-populated tuple (target id
            -- set, state NULL) makes every `state = '...'` comparison evaluate to NULL, so the
            -- whole constraint yields NULL and the invalid row is ACCEPTED.
            CHECK ((ExpectedTargetLocalPrincipalId IS NULL
                    AND ExpectedTargetState IS NULL
                    AND ExpectedTargetPublicVerificationKey IS NULL)
                OR (ExpectedTargetLocalPrincipalId IS NOT NULL
                    AND ExpectedTargetState IS NOT NULL
                    AND ((ExpectedTargetState = 'Active'  AND ExpectedTargetPublicVerificationKey IS NOT NULL)
                      OR (ExpectedTargetState = 'Revoked' AND ExpectedTargetPublicVerificationKey IS NULL))))
        );

        -- SS4d, IDENT-002: rows are this Host's own servers, but AuthoritativeHostId is stored
        -- explicitly and never omitted just because it is the trivial/local case, so a unified
        -- local+remote inventory view never has to special-case which rows are "ours".
        CREATE TABLE ServerInventory (
            ServerProfileId     TEXT NOT NULL,
            AuthoritativeHostId TEXT NOT NULL,
            DisplayName         TEXT NOT NULL,
            InstallPath         TEXT     NULL,
            -- SS6 lists "ports" among this table's contents, and the v0.4 ServerProfile this
            -- table is the evolution of already fixes exactly which two, with these defaults -
            -- so a profile with non-default ports round-trips instead of silently reverting to
            -- 8211/8212 and launching or polling the wrong endpoint.
            GamePort            INTEGER NOT NULL DEFAULT 8211,
            RestApiPort         INTEGER NOT NULL DEFAULT 8212,
            ImportProvenance    TEXT     NULL,
            CreatedUtc          TEXT NOT NULL,
            PRIMARY KEY (AuthoritativeHostId, ServerProfileId)
        );

        -- SS4a/SS4a-i/SS8. Paired peer ManagerIdentity rows. Revoked rows are retained as
        -- tombstones (SS8), never deleted - including a PeerBound row that expired before
        -- reaching Active. PeerHostId (stable identity) is kept distinct from the credential
        -- fingerprints (IDENT-001), and staged-rotation state is separate from current trust
        -- (IDENT-003). PeerRecoveryRequired supports IDENT-004.
        CREATE TABLE TrustedManagers (
            PeerHostId                         TEXT PRIMARY KEY,
            State                              TEXT NOT NULL CHECK (State IN ('PeerBound','Active','Revoked')),
            CurrentTrustedPublicKeyFingerprint TEXT     NULL,
            PendingTrustedPublicKeyFingerprint TEXT     NULL,
            PendingRotationId                  TEXT     NULL,
            PendingRotationExpiresUtc          TEXT     NULL,
            PendingReconfirmationRequired      INTEGER NOT NULL DEFAULT 0 CHECK (PendingReconfirmationRequired IN (0,1)),
            PeerRecoveryRequired               INTEGER NOT NULL DEFAULT 0 CHECK (PeerRecoveryRequired IN (0,1)),
            DisplayName                        TEXT     NULL,
            MachineName                        TEXT     NULL,
            PairedUtc                          TEXT     NULL,
            RevokedUtc                         TEXT     NULL,
            CreatedUtc                         TEXT NOT NULL,
            -- SS4b/SS4a: a PeerBound or Active row represents an actually pinned peer
            -- credential, so it must have one.
            CHECK (State = 'Revoked' OR CurrentTrustedPublicKeyFingerprint IS NOT NULL),
            -- SS8: a Revoked tombstone retains the FACT that this HostId was once trusted, but
            -- never a dangling credential, staged rotation, or moot recovery flag - revocation
            -- clears all of them in the same transaction, so no other combination is valid.
            CHECK (State <> 'Revoked' OR (
                    CurrentTrustedPublicKeyFingerprint IS NULL
                AND PendingTrustedPublicKeyFingerprint IS NULL
                AND PendingRotationId                  IS NULL
                AND PendingRotationExpiresUtc          IS NULL
                AND PendingReconfirmationRequired      = 0
                AND PeerRecoveryRequired               = 0))
        );

        -- SS4b-i, PAIR-004. Bounded, ZERO-AUTHORITY candidates awaiting explicit Owner approval
        -- when a fresh pairing claims an already-known HostId. Deliberately carries no grant
        -- linkage of any kind: prior grants never silently reactivate through this table.
        CREATE TABLE PendingCredentialReplacements (
            ReplacementId          TEXT PRIMARY KEY,
            PeerHostId             TEXT NOT NULL REFERENCES TrustedManagers(PeerHostId),
            -- SS4b-i's CandidatePublicKeyFingerprint.
            ProposedKeyFingerprint TEXT NOT NULL,
            VerifiedUtc            TEXT NOT NULL,
            ApprovedByOwnerLocalPrincipalId TEXT NULL REFERENCES LocalPrincipals(LocalPrincipalId),
            ApprovedUtc            TEXT     NULL,
            ExpiresUtc             TEXT NOT NULL,
            -- SS4b-i: the target TrustedManagers row's State and CurrentTrustedPublicKeyFingerprint
            -- exactly as they stood at candidate-creation time. PendingRotationId is deliberately
            -- NOT captured as a third expected value - SS4b-i decided against it explicitly.
            ExpectedTrustState     TEXT NOT NULL CHECK (ExpectedTrustState IN ('PeerBound','Active','Revoked')),
            ExpectedCurrentTrustedPublicKeyFingerprint TEXT NULL,
            InvalidatedUtc         TEXT     NULL,
            CreatedUtc             TEXT NOT NULL,
            -- Approval is a single fact: who approved and when are recorded together, so the row
            -- can never claim half an approval.
            CHECK ((ApprovedUtc IS NULL     AND ApprovedByOwnerLocalPrincipalId IS NULL)
                OR (ApprovedUtc IS NOT NULL AND ApprovedByOwnerLocalPrincipalId IS NOT NULL)),
            -- The snapshot captures a real TrustedManagers row, so its valid state/fingerprint
            -- combinations mirror that table's own: a pinned peer had a pin, a revoked one
            -- did not.
            CHECK ((ExpectedTrustState IN ('PeerBound','Active') AND ExpectedCurrentTrustedPublicKeyFingerprint IS NOT NULL)
                OR (ExpectedTrustState = 'Revoked'               AND ExpectedCurrentTrustedPublicKeyFingerprint IS NULL))
        );

        -- SS4a-i, IDENT-003. In-progress routine rotation state, with per-peer staging tracked
        -- in its own child table (a peer reaching Active mid-rotation is added dynamically).
        CREATE TABLE HostCredentialRotations (
            RotationId       TEXT PRIMARY KEY,
            OldCredentialRef TEXT     NULL,
            NewCredentialRef TEXT     NULL,
            -- SS4a-i's states, including 'Prepared': step 1 prepares a rotation (securing
            -- NewCredentialRef) before any peer staging begins, and that nonterminal state must
            -- be representable for step 1's "idempotent and serialized against any other
            -- nonterminal rotation" rule to be enforceable at all.
            State            TEXT NOT NULL CHECK (State IN ('Prepared','Staging','ReadyForCutover','CutOver','Completed','Aborted')),
            StartedUtc       TEXT NOT NULL,
            CutOverUtc       TEXT     NULL,
            CompletedUtc     TEXT     NULL
        );

        -- SS4a's CredentialHistory[]: each peer's PRIOR credentials as observed by THIS Host,
        -- appended when that peer promotes a staged credential (SS4a-i step 6). Distinct from
        -- HostCredentialRotations, which describes this Host's OWN rotation and per-peer
        -- progress. Retained for audit only.
        --
        -- SS4a-i step 6 fixes that history records only fingerprints and timestamps, NEVER a
        -- RotationId - so no RotationId column exists here, deliberately.
        CREATE TABLE TrustedManagerCredentialHistory (
            CredentialHistoryId       TEXT PRIMARY KEY,
            PeerHostId                TEXT NOT NULL REFERENCES TrustedManagers(PeerHostId),
            PriorPublicKeyFingerprint TEXT NOT NULL,
            RotatedUtc                TEXT NOT NULL
        );

        CREATE INDEX IX_TrustedManagerCredentialHistory_Peer ON TrustedManagerCredentialHistory (PeerHostId, RotatedUtc);

        CREATE TABLE HostCredentialRotationPeers (
            RotationId      TEXT NOT NULL REFERENCES HostCredentialRotations(RotationId),
            PeerHostId      TEXT NOT NULL REFERENCES TrustedManagers(PeerHostId),
            StagedUtc       TEXT     NULL,
            AcknowledgedUtc TEXT     NULL,
            PromotedUtc     TEXT     NULL,
            PRIMARY KEY (RotationId, PeerHostId)
        );

        -- SS5, SS5b. Host- and server-capability grants are kept as two separate tables so a
        -- Host-level capability can never become a server-scoped grant, and vice versa
        -- (AUTH-001: type/scope valid BY CONSTRUCTION, not by validation code).
        --
        -- AUTH-002: DerivedFromGrantId is a single nullable self-reference - a single-parent
        -- grant forest, never a multi-parent DAG.
        -- AUTH-005: every HostCapabilityGrant targets exactly one HostId (NOT NULL).
        CREATE TABLE HostCapabilityGrants (
            GrantId             TEXT PRIMARY KEY,
            TargetHostId        TEXT NOT NULL,
            Capability          TEXT NOT NULL,
            GranteeActorKind    TEXT NOT NULL CHECK (GranteeActorKind IN ('LocalPrincipal','RemoteManager')),
            GranteeLocalPrincipalId TEXT NULL REFERENCES LocalPrincipals(LocalPrincipalId),
            GranteePeerHostId   TEXT     NULL REFERENCES TrustedManagers(PeerHostId),
            GrantedByActorKind  TEXT NOT NULL CHECK (GrantedByActorKind IN ('LocalPrincipal','RemoteManager')),
            GrantedByLocalPrincipalId TEXT NULL REFERENCES LocalPrincipals(LocalPrincipalId),
            GrantedByPeerHostId TEXT     NULL REFERENCES TrustedManagers(PeerHostId),
            CanDelegate         INTEGER NOT NULL CHECK (CanDelegate IN (0,1)),
            CanDelegateOnwardDelegation INTEGER NOT NULL CHECK (CanDelegateOnwardDelegation IN (0,1)),
            DerivedFromGrantId  TEXT     NULL REFERENCES HostCapabilityGrants(GrantId),
            CreatedUtc          TEXT NOT NULL,
            InvalidatedUtc      TEXT     NULL,
            -- SS5b ActorRef: exactly one actor shape populated on each side.
            CHECK ((GranteeActorKind = 'LocalPrincipal' AND GranteeLocalPrincipalId IS NOT NULL AND GranteePeerHostId IS NULL)
                OR (GranteeActorKind = 'RemoteManager'  AND GranteePeerHostId    IS NOT NULL AND GranteeLocalPrincipalId IS NULL)),
            CHECK ((GrantedByActorKind = 'LocalPrincipal' AND GrantedByLocalPrincipalId IS NOT NULL AND GrantedByPeerHostId IS NULL)
                OR (GrantedByActorKind = 'RemoteManager'  AND GrantedByPeerHostId    IS NOT NULL AND GrantedByLocalPrincipalId IS NULL)),
            -- SS5: onward-delegation authority can never exceed delegation authority.
            CHECK (CanDelegate = 1 OR CanDelegateOnwardDelegation = 0)
        );

        CREATE TABLE ServerCapabilityGrants (
            GrantId             TEXT PRIMARY KEY,
            AuthoritativeHostId TEXT NOT NULL,
            ServerProfileId     TEXT NOT NULL,
            Capability          TEXT NOT NULL,
            GranteeActorKind    TEXT NOT NULL CHECK (GranteeActorKind IN ('LocalPrincipal','RemoteManager')),
            GranteeLocalPrincipalId TEXT NULL REFERENCES LocalPrincipals(LocalPrincipalId),
            GranteePeerHostId   TEXT     NULL REFERENCES TrustedManagers(PeerHostId),
            GrantedByActorKind  TEXT NOT NULL CHECK (GrantedByActorKind IN ('LocalPrincipal','RemoteManager')),
            GrantedByLocalPrincipalId TEXT NULL REFERENCES LocalPrincipals(LocalPrincipalId),
            GrantedByPeerHostId TEXT     NULL REFERENCES TrustedManagers(PeerHostId),
            CanDelegate         INTEGER NOT NULL CHECK (CanDelegate IN (0,1)),
            CanDelegateOnwardDelegation INTEGER NOT NULL CHECK (CanDelegateOnwardDelegation IN (0,1)),
            DerivedFromGrantId  TEXT     NULL REFERENCES ServerCapabilityGrants(GrantId),
            CreatedUtc          TEXT NOT NULL,
            InvalidatedUtc      TEXT     NULL,
            CHECK ((GranteeActorKind = 'LocalPrincipal' AND GranteeLocalPrincipalId IS NOT NULL AND GranteePeerHostId IS NULL)
                OR (GranteeActorKind = 'RemoteManager'  AND GranteePeerHostId    IS NOT NULL AND GranteeLocalPrincipalId IS NULL)),
            CHECK ((GrantedByActorKind = 'LocalPrincipal' AND GrantedByLocalPrincipalId IS NOT NULL AND GrantedByPeerHostId IS NULL)
                OR (GrantedByActorKind = 'RemoteManager'  AND GrantedByPeerHostId    IS NOT NULL AND GrantedByLocalPrincipalId IS NULL)),
            CHECK (CanDelegate = 1 OR CanDelegateOnwardDelegation = 0)
        );

        -- SS6, SS5b. ActorRef-keyed audit trail. NEVER secrets (SEC-001) - this table has no
        -- column shaped to carry secret material, and later writers apply [Secret] redaction.
        CREATE TABLE AuditEvents (
            AuditEventId          TEXT PRIMARY KEY,
            OccurredUtc           TEXT NOT NULL,
            EventKind             TEXT NOT NULL,
            ActorKind             TEXT     NULL CHECK (ActorKind IS NULL OR ActorKind IN ('LocalPrincipal','RemoteManager','OfflineRecovery')),
            ActorLocalPrincipalId TEXT     NULL,
            ActorPeerHostId       TEXT     NULL,
            AffectedHostId        TEXT     NULL,
            AffectedServerProfileId TEXT   NULL,
            IsOfflineRecovery     INTEGER NOT NULL DEFAULT 0 CHECK (IsOfflineRecovery IN (0,1)),
            Summary               TEXT     NULL
        );

        -- SS10, OPS-004. Every durable operation has an EXPLICIT discriminated target - never a
        -- bare or synthetic ServerRef, and never inferred from Kind alone.
        CREATE TABLE OperationRecords (
            OperationId         TEXT PRIMARY KEY,
            Kind                TEXT NOT NULL,
            TargetKind          TEXT NOT NULL CHECK (TargetKind IN ('HostTarget','ServerTarget')),
            TargetHostId        TEXT     NULL,
            TargetServerProfileId TEXT   NULL,
            Phase               TEXT NOT NULL,
            IsTerminal          INTEGER NOT NULL DEFAULT 0 CHECK (IsTerminal IN (0,1)),
            RecoveryDisposition TEXT     NULL CHECK (RecoveryDisposition IS NULL OR RecoveryDisposition IN ('SafeToRetryFromStart','SafeToResumeFromPhase','RequiresManualReview','SafeToDiscard')),
            StartedUtc          TEXT NOT NULL,
            LastHeartbeatUtc    TEXT     NULL,
            -- HostTarget carries only a HostId; ServerTarget carries a Host-qualified ServerRef
            -- (IDENT-002 - the AuthoritativeHostId is never omitted).
            CHECK ((TargetKind = 'HostTarget'   AND TargetHostId IS NOT NULL AND TargetServerProfileId IS NULL)
                OR (TargetKind = 'ServerTarget' AND TargetHostId IS NOT NULL AND TargetServerProfileId IS NOT NULL))
        );

        -- SS9, OPS-002/OPS-004. Lock scope is deliberately INDEPENDENT of the operation's target:
        -- a server-targeted operation may legitimately hold a HostScope lock.
        --
        -- OwningOperationId is a NOT NULL foreign key into OperationRecords, so a durable lock
        -- can never exist without the record startup reconciliation needs in order to classify
        -- it - the accepted atomic record+lock relationship, enforced structurally.
        CREATE TABLE OperationLocks (
            OperationLockId     TEXT PRIMARY KEY,
            ScopeKind           TEXT NOT NULL CHECK (ScopeKind IN ('HostScope','ServerScope')),
            ScopeHostId         TEXT NOT NULL,
            ScopeServerProfileId TEXT    NULL,
            OperationKind       TEXT NOT NULL,
            OwningOperationId   TEXT NOT NULL REFERENCES OperationRecords(OperationId),
            AcquiredUtc         TEXT NOT NULL,
            CHECK ((ScopeKind = 'HostScope'   AND ScopeServerProfileId IS NULL)
                OR (ScopeKind = 'ServerScope' AND ScopeServerProfileId IS NOT NULL))
        );

        -- SS9, OPS-001. Revision tokens for optimistic concurrency. The stale-write COMPARISON
        -- is #46's engine; this is the storage shape it will use.
        CREATE TABLE ConfigurationRevisions (
            ResourceKind    TEXT NOT NULL,
            ResourceId      TEXT NOT NULL,
            RevisionId      INTEGER NOT NULL CHECK (RevisionId >= 0),
            LastModifiedUtc TEXT NOT NULL,
            PRIMARY KEY (ResourceKind, ResourceId)
        );

        -- SS7, SEC-001. OPAQUE references/metadata pointing into the platform secure store. The
        -- actual secret bytes never enter this database - there is no column here shaped to
        -- hold them.
        CREATE TABLE SecureCredentialReferences (
            CredentialRef TEXT PRIMARY KEY,
            Purpose       TEXT NOT NULL,
            CreatedUtc    TEXT NOT NULL,
            RetiredUtc    TEXT     NULL
        );

        -- MIG-001. v0.4 -> v0.5 migration AUDIT TRAIL only. #40 performs no data migration; the
        -- existence of this table does not imply migration execution.
        CREATE TABLE MigrationRecords (
            MigrationRecordId TEXT PRIMARY KEY,
            SourceDescription TEXT NOT NULL,
            StartedUtc        TEXT NOT NULL,
            CompletedUtc      TEXT     NULL,
            Outcome           TEXT     NULL,
            Notes             TEXT     NULL
        );

        CREATE INDEX IX_HostCapabilityGrants_Grantee ON HostCapabilityGrants (GranteeLocalPrincipalId, GranteePeerHostId);
        CREATE INDEX IX_ServerCapabilityGrants_Grantee ON ServerCapabilityGrants (GranteeLocalPrincipalId, GranteePeerHostId);
        CREATE INDEX IX_OperationRecords_NonTerminal ON OperationRecords (IsTerminal) WHERE IsTerminal = 0;
        CREATE UNIQUE INDEX UX_OperationLocks_HostScope ON OperationLocks (ScopeHostId) WHERE ScopeKind = 'HostScope';
        CREATE UNIQUE INDEX UX_OperationLocks_ServerScope ON OperationLocks (ScopeHostId, ScopeServerProfileId) WHERE ScopeKind = 'ServerScope';
        CREATE INDEX IX_AuditEvents_OccurredUtc ON AuditEvents (OccurredUtc);
        """;
}

public static class HostSchema
{
    public static IReadOnlyList<IHostSchemaMigration> AllMigrations() =>
    [
        new Migration001InitialHostSchema(),
    ];
}
