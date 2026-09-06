using System.IO.Compression;
using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;
using PalworldServerManager.Core.Services;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.SelfTest;

// A harness mode so this already-built apphost binary can stand in for a "PalServer.exe" that
// sleeps for a controlled duration and exits with a controlled code, for synthetic process
// reattachment tests. This avoids relying on renamed OS utilities (renaming cmd.exe breaks its
// own argument handling) while still exercising a real, running, real-PID Windows process.
if (args.Length > 0)
{
    if (args is ["--local-security-rpc-probe"])
    {
        await ProtocolTests.SchemaEvolution();
        await LocalSecurityRpcTests.NegotiationAndBootstrap();
        await LocalSecurityRpcTests.AuthenticationAndAuthority();
        await LocalSecurityRpcTests.RecoveryAndRollback();
        await LocalSecurityRpcTests.LimitsAndScope();
        Console.WriteLine("PASS local security RPC probe"); return 0;
    }
    if (args.Length == 1 && args[0] == "--host-trust-reconciliation-probe")
    {
        await HostTrustReconciliationTests.MigrationAndProjection();
        await HostTrustReconciliationTests.RecoveryMetadataAndRollback();
        await HostTrustReconciliationTests.ReconciliationFailureOrdering();
        await HostTrustReconciliationTests.MaterialAndNoOldPrivateRead();
        return 0;
    }
    if (args.Length == 1 && args[0] == "--owner-recovery-probe")
    {
        await OwnerRecoveryTests.RotationAndRetry();
        await OwnerRecoveryTests.RehomeTargetsAndGrantForest();
        await OwnerRecoveryTests.StaleSnapshotsAndAba();
        await OwnerRecoveryTests.OnlineCompletionBoundary();
        return 0;
    }
    if (args.Length == 1 && args[0] == "--local-enrollment-probe")
    {
        await LocalEnrollmentTests.BootstrapAndRetries();
        await LocalEnrollmentTests.EnrollmentAttemptsAndConcurrency();
        await LocalEnrollmentTests.RevocationAndAba();
        await LocalEnrollmentTests.HostBoundaryAndRedaction();
        return 0;
    }
    if (args.Length == 1 && args[0] == "--local-principal-probe")
    {
        await LocalPrincipalAuthenticationTests.MappingAndBoundaries();
        await LocalPrincipalAuthenticationTests.NonceAndLifetime();
        await LocalPrincipalAuthenticationTests.RevocationAndMalformedProofs();
        await LocalPrincipalAuthenticationTests.ClientKeyAndFrame();
        return 0;
    }
    if (args.Length == 1 && args[0] == "--native-tls-cache-probe") { await NativeTlsCacheTests.Lifecycle(); return 0; }
    if (args.Length == 1 && args[0] == "--local-trust-probe") { await LocalTrustTests.Schema(); await LocalTrustTests.FilesAndTls(); return 0; }
    if (args.Length == 1 && args[0] == "--local-ipc-spike") { await LocalIpcSpike.LocalProof(); return 0; }
    if (args[0].StartsWith("--windows-", StringComparison.Ordinal))
        return await WindowsIntegration.RunAsync(args);
    if (args.Length == 3 && args[0] == "--harness")
    {
        var seconds = int.Parse(args[1]);
        var exitCode = int.Parse(args[2]);
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        return exitCode;
    }

    // Cross-process modes for #40's machine-wide exclusivity-lock tests (SS2/SS5a). A real second
    // OS process is the only honest way to prove machine-wide exclusion, abandonment recovery,
    // and Host.Cli's refusal while Host holds the lock.
    if (args.Length == 2 && args[0] == "--lock-try")
    {
        // Attempt acquisition and report the outcome, then release immediately.
        using var attempt = HostExclusivityLock.TryAcquire(TimeSpan.FromMilliseconds(500), args[1]);
        Console.WriteLine(attempt is not null ? "ACQUIRED" : "DENIED");
        return 0;
    }

    if (args.Length == 3 && args[0] == "--lock-hold")
    {
        // Acquire, announce, hold for a bounded time, then release normally.
        using var held = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(5), args[1]);
        Console.WriteLine(held is not null ? "ACQUIRED" : "DENIED");
        Console.Out.Flush();
        Thread.Sleep(TimeSpan.FromSeconds(int.Parse(args[2])));
        return 0;
    }

    if (args.Length == 2 && args[0] == "--lock-abandon")
    {
        // Acquire, then die WITHOUT releasing, to exercise the abandoned-mutex path.
        var abandoned = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(5), args[1]);
        Console.WriteLine(abandoned is not null ? "ACQUIRED" : "DENIED");
        Console.Out.Flush();
        Environment.Exit(0);
    }

    // Some tests also copy this apphost in as a stand-in "PalServer.exe" and let real production
    // code (ServerProcessService.StartAsync) launch it with its own real arguments (e.g.
    // "-port=8211") to prove operation-tracker wiring. Any non-harness arguments mean that case:
    // exit immediately rather than accidentally running the full self-test suite as an unwanted
    // recursive child process.
    return 0;
}

var tests = new List<(string Name, Func<Task> Run)>
{
    ("Local security gRPC requires TLS negotiation and intended-user bootstrap", LocalSecurityRpcTests.NegotiationAndBootstrap),
    ("Local security gRPC binds native identity nonces and Owner authorization", LocalSecurityRpcTests.AuthenticationAndAuthority),
    ("Local security gRPC recovery preserves transactions audits and Owner identity", LocalSecurityRpcTests.RecoveryAndRollback),
    ("Local security gRPC limits payloads and exposes no privileged preparation RPC", LocalSecurityRpcTests.LimitsAndScope),
    ("Client ceremony retries and rotation preserve exact durable keys", ClientCredentialCeremonyTests.RetryAndRotation),
    ("Client re-home key choice and explicit discard preserve current binding", ClientCredentialCeremonyTests.RehomeAndDiscard),
    ("Client ceremony failed writes cancellation and v1 migration preserve state", ClientCredentialCeremonyTests.FailureAndMigration),
    ("Client ticket history preserves keys across Host consumed-result retries", ClientCredentialCeremonyTests.HostConsumedRetry),
    ("Offline command surface is bounded and has no online fallback", OfflineCoordinatorTests.CommandBoundary),
    ("Committed publication retry retains lease through cancellation and broken diagnostics", OfflineCoordinatorTests.PublicationBarrier),
    ("Owner handoff framing binds exact Host ticket and purpose without implicit secret export", OwnerHandoffTests.Format),
    ("Host trust metadata upgrade and every rotation state project authoritative pins", HostTrustReconciliationTests.MigrationAndProjection),
    ("Machine recovery atomically updates current rotation and peer recovery metadata", HostTrustReconciliationTests.RecoveryMetadataAndRollback),
    ("Host trust reconciliation publishes before idempotent tracked retirement", HostTrustReconciliationTests.ReconciliationFailureOrdering),
    ("Machine recovery never reads old private material or retires enrollment HMAC key", HostTrustReconciliationTests.MaterialAndNoOldPrivateRead),
    ("Owner rotation preserves identity and permits indefinite consumed retries", OwnerRecoveryTests.RotationAndRetry),
    ("Owner re-home revokes the old row and preserves independent grant roots", OwnerRecoveryTests.RehomeTargetsAndGrantForest),
    ("Owner recovery rejects stale snapshots and full credential and target ABA", OwnerRecoveryTests.StaleSnapshotsAndAba),
    ("Online recovery completion uses protected verifier and distinct audit events", OwnerRecoveryTests.OnlineCompletionBoundary),
    ("Owner bootstrap transactions roll back and concurrent consumed retries preserve identity", LocalEnrollmentTests.BootstrapAndRetries),
    ("Enrollment retries and concurrent wrong-code attempts preserve bounded authority", LocalEnrollmentTests.EnrollmentAttemptsAndConcurrency),
    ("Local revocation and reactivation invalidate stale tickets and grant descendants", LocalEnrollmentTests.RevocationAndAba),
    ("Host enrollment requires current Owner and redacts bearer and verifier material", LocalEnrollmentTests.HostBoundaryAndRedaction),
    ("Local principal authentication maps exact active identity and native user", LocalPrincipalAuthenticationTests.MappingAndBoundaries),
    ("Local principal nonces reject replay cross-connection expiry and concurrent reuse", LocalPrincipalAuthenticationTests.NonceAndLifetime),
    ("Local principal authentication rejects revoked changed and malformed proofs", LocalPrincipalAuthenticationTests.RevocationAndMalformedProofs),
    ("Production client keys and challenge frames interoperate without private Host material", LocalPrincipalAuthenticationTests.ClientKeyAndFrame),
    ("Local public trust artifact rejects ambiguous and unsupported schemas", LocalTrustTests.Schema),
    ("Local trust path and TLS verify identity pins before sensitive requests", LocalTrustTests.FilesAndTls),
    ("Local IPC spike proves named-pipe TLS native SID and pre-request pin rejection", LocalIpcSpike.LocalProof),
    ("Protocol wire/domain server identities preserve Host qualification", ProtocolTests.Identities),
    ("Secure credential store lifecycle contract survives reopen and cancellation", SecureStoreTests.Lifecycle),
    ("Machine credential store rejects tamper swaps plaintext and failed replacement", SecureStoreTests.NegativePaths),
    ("Machine credential store rejects unsafe ACL and missing recovery identities", SecureStoreTests.AclRejection),
    ("Secret-marked values redact logging JSON and diagnostics", SecureStoreTests.Redaction),
    ("Credential writers serialize across store instances and normalized roots", SecureStoreTests.ConcurrentWriters),
    ("Protocol negotiation gates features independently of product versions", ProtocolTests.Negotiation),
    ("Protocol unknown fields survive and unknown authority values deny", ProtocolTests.UnknownValues),
    ("Protocol schema evolution preserves fields/enums/RPCs and removed reservations", ProtocolTests.SchemaEvolution),
    ("Windows activation is idempotent and classifies native errors", WindowsPlatformTests.Activation),
    ("Windows platform DACL and root policy are least privilege", WindowsPlatformTests.SecurityPolicy),
    ("Existing protected Host state requires service access and privileged ownership", WindowsPlatformTests.ExistingStateAcl),
    ("Windows login command quoting uses a test registry", WindowsPlatformTests.LoginStart),
    ("Windows shell only opens bounded local directories through a fake launcher", WindowsPlatformTests.Shell),
    ("Client CurrentUser DPAPI complete credential lifecycle and shared binding", WindowsPlatformTests.CredentialLifecycle),
    ("Client credential atomic-write failure preserves last good state", WindowsPlatformTests.CredentialWriteFailure),
    ("Concurrent client stores converge on one credential", WindowsPlatformTests.ConcurrentCredentials),
    ("Host runtime holds and deterministically releases database and lock", WindowsPlatformTests.Runtime),
    ("Config parser handles quoted commas and nested lists", TestConfigParser),
    ("Config round-trip preserves unknown settings", TestUnknownRoundTrip),
    ("Directory copy leaves source byte-for-byte unchanged", TestNonDestructiveCopy),
    ("Profile registry round-trips", TestProfileRegistry),
    ("Manual discovery recognizes a legacy server", TestDiscovery),
    ("Structured logger records correlated operations", TestStructuredLogging),
    ("SteamCMD code 7 is classified for interactive recovery", TestSteamCmdRecoveryClassification),
    ("Server lifetime result prefers shipping-process exit code", TestServerLifetimeExitResult),
    ("Diagnostic bundle redacts secrets and excludes saves", TestDiagnosticBundle),
    ("Palworld REST models parse representative JSON", RestTests.TestRestModelsParseRepresentativeJson),
    ("Palworld REST models tolerate missing/partial JSON fields", RestTests.TestRestModelsToleratePartialJson),
    ("Palworld REST settings redact secret-shaped keys", RestTests.TestRestSettingsRedaction),
    ("Palworld REST client never logs the admin password", RestTests.TestRestSecretsNeverLogged),
    ("Pairing code is six digits and one-use", LanTests.TestPairingCodeIsSixDigitsAndOneUse),
    ("Pairing wrong code does not consume the real code", LanTests.TestPairingWrongCodeDoesNotConsumeTheRealCode),
    ("Pairing failed attempts are bounded and lock out the code", LanTests.TestPairingFailedAttemptsAreBoundedAndLockOutTheCode),
    ("LAN is disabled by default for a new Manager state", LanTests.TestLanDisabledByDefaultForANewState),
    ("Trusted-peer token is hashed at rest and revocable", LanTests.TestTrustedPeerTokenIsHashedAtRestAndAuthorizesOnlyUntilRevoked),
    ("Remote pairing credential persists across a Manager restart", LanTests.TestRemoteCredentialPersistsAcrossReload),
    ("LAN discovery advertisement carries no secrets", LanTests.TestDiscoveryAdvertisementCarriesNoSecrets),
    ("LAN discovery filters unknown protocol/version/self advertisements", LanTests.TestDiscoveryFiltersUnknownProtocolAndSelfAdvertisements),
    ("LAN API rejects unauthenticated and wrong-token requests", LanTests.TestLanHostRejectsUnauthenticatedAndWrongTokenRequests),
    ("LAN pairing grants authorized access and rejects a wrong code", LanTests.TestLanPairingGrantsAuthorizedAccessAndRejectsWrongCode),
    ("LAN transfer offer rejects malformed metadata", LanTests.TestLanTransferOfferRejectsMalformedMetadata),
    ("LAN transfer completes and verifies whole-file SHA-256", LanTests.TestLanTransferCompletesAndVerifiesWholeFileHash),
    ("LAN transfer hash mismatch is rejected and leaves no partial file", LanTests.TestLanTransferHashMismatchIsRejectedAndLeavesNoPartialFile),
    ("LAN transfer receive registers as a LanTransferReceive critical operation", LanTests.TestLanTransferReceiveRegistersAsLanTransferReceiveOperation),
    ("Identity matcher rejects PID reuse via start-time mismatch", RuntimeReattachmentTests.TestIdentityMatcherRejectsPidReuseAcrossStartTimeMismatch),
    ("Identity matcher rejects executable-path mismatch", RuntimeReattachmentTests.TestIdentityMatcherRejectsExecutablePathMismatch),
    ("Identity matcher rejects unrecognized process names", RuntimeReattachmentTests.TestIdentityMatcherRejectsUnrecognizedProcessName),
    ("Identity matcher accepts a fully verified match", RuntimeReattachmentTests.TestIdentityMatcherAcceptsFullyVerifiedMatch),
    ("Runtime handoff round-trips and is one-shot", RuntimeReattachmentTests.TestRuntimeHandoffRoundTripsAndIsOneShot),
    ("Runtime handoff contains no secret-shaped fields", RuntimeReattachmentTests.TestRuntimeHandoffContainsNoSecretShapedFields),
    ("Runtime handoff rejects a stale file", RuntimeReattachmentTests.TestRuntimeHandoffRejectsStaleFile),
    ("Runtime handoff DeleteAsync is safe and idempotent when no file exists", RuntimeReattachmentTests.TestRuntimeHandoffDeleteAsyncIsSafeAndIdempotentWhenNoFileExists),
    ("Runtime handoff DeleteAsync removes only the handoff file", RuntimeReattachmentTests.TestRuntimeHandoffDeleteAsyncRemovesOnlyTheHandoffFile),
    ("Runtime handoff rejects an unsupported format version", RuntimeReattachmentTests.TestRuntimeHandoffRejectsUnsupportedFormatVersion),
    ("Reconcile attaches to an already-running process and captures its exit code", RuntimeReattachmentTests.TestReconcileAttachesToAlreadyRunningProcessAndCapturesExitCode),
    ("Reconcile falls back to a path scan when a handoff hint does not verify", RuntimeReattachmentTests.TestReconcileFallsBackToPathScanWhenHandoffHintDoesNotVerify),
    ("Reconcile reports an honest gap-exit when a handoff expected a server that is gone", RuntimeReattachmentTests.TestReconcileReportsExitedDuringGapWhenHandoffExpectedButNothingIsRunning),
    ("Reconcile reports NotRunning when nothing matches", RuntimeReattachmentTests.TestReconcileReturnsNotRunningWhenNothingMatches),
    ("Reconcile does not cross-attach different managed profiles", RuntimeReattachmentTests.TestReconcileDoesNotCrossAttachDifferentManagedProfiles),
    ("Full restart handoff cycle reattaches and captures the exact exit code", RuntimeReattachmentTests.TestFullRestartHandoffCycleReattachesAndCapturesExitCode),
    ("Execution mode detector prefers Installed over everything", ApplicationUpdateServiceTests.TestExecutionModeDetectorPrefersInstalledOverEverything),
    ("Execution mode detector recognizes Velopack portable", ApplicationUpdateServiceTests.TestExecutionModeDetectorRecognizesVelopackPortable),
    ("Execution mode detector recognizes a development build by sibling .csproj", ApplicationUpdateServiceTests.TestExecutionModeDetectorRecognizesDevelopmentBuildBySiblingCsproj),
    ("Execution mode detector defaults to Portable when ambiguous", ApplicationUpdateServiceTests.TestExecutionModeDetectorDefaultsToPortableWhenAmbiguous),
    ("Update check is skipped when not installed", ApplicationUpdateServiceTests.TestCheckIsSkippedWhenNotInstalled),
    ("Default update channel is Stable", ApplicationUpdateServiceTests.TestDefaultChannelIsStable),
    ("Update channel persists across service instances", ApplicationUpdateServiceTests.TestChannelPersistsAcrossServiceInstances),
    ("Fresh install defaults channel from the installed package", ApplicationUpdateServiceTests.TestFreshInstallDefaultsChannelFromTheInstalledPackage),
    ("Fresh install defaults to Stable when installed channel is unknown", ApplicationUpdateServiceTests.TestFreshInstallDefaultsToStableWhenInstalledChannelIsUnknown),
    ("Explicitly saved channel preference overrides the installed package on reload", ApplicationUpdateServiceTests.TestExplicitlySavedChannelPreferenceOverridesTheInstalledPackageOnReload),
    ("Changing update channel invalidates cached availability", ApplicationUpdateServiceTests.TestChangingChannelInvalidatesCachedAvailability),
    ("Update check passes the currently selected channel to the backend", ApplicationUpdateServiceTests.TestCheckPassesTheCurrentlySelectedChannelToTheBackend),
    ("Update state: Idle to Checking to Idle when no update is found", ApplicationUpdateServiceTests.TestIdleCheckingIdleWhenNoUpdateFound),
    ("Update state: Idle to Checking to UpdateAvailable", ApplicationUpdateServiceTests.TestIdleCheckingUpdateAvailable),
    ("Update state: UpdateAvailable to Downloading to ReadyToInstall", ApplicationUpdateServiceTests.TestUpdateAvailableDownloadingReadyToInstall),
    ("Download with nothing staged is a no-op", ApplicationUpdateServiceTests.TestDownloadWithNothingStagedIsANoOp),
    ("Update check failure transitions to Failed", ApplicationUpdateServiceTests.TestCheckFailureTransitionsToFailed),
    ("Update download failure transitions to Failed", ApplicationUpdateServiceTests.TestDownloadFailureTransitionsToFailed),
    ("Retry from Failed succeeds", ApplicationUpdateServiceTests.TestRetryFromFailedSucceeds),
    ("Overlapping update check is rejected, not queued", ApplicationUpdateServiceTests.TestOverlappingCheckIsRejectedNotQueued),
    ("Overlapping update download is rejected, not queued", ApplicationUpdateServiceTests.TestOverlappingDownloadIsRejectedNotQueued),
    ("ApplicationUpdateService has no PalworldRestClient dependency", ApplicationUpdateServiceTests.TestApplicationUpdateServiceHasNoPalworldRestClientDependency),
    ("Checking and downloading never write a runtime handoff", ApplicationUpdateServiceTests.TestCheckingAndDownloadingNeverWriteARuntimeHandoff),
    ("Update check failure is logged as an error", ApplicationUpdateServiceTests.TestCheckFailureIsLoggedAsAnError),
    ("Applying an update does not stop a synthetic running server", ApplicationUpdateServiceTests.TestApplyingDoesNotStopASyntheticRunningServer),
    ("Apply is blocked by each critical operation kind and allowed once idle", ApplicationUpdateServiceTests.TestApplyIsBlockedByEachCriticalOperationKindAndAllowedOnceIdle),
    ("A running server alone does not block apply", ApplicationUpdateServiceTests.TestARunningServerAloneDoesNotBlockApply),
    ("A failed apply attempt leaves the update ReadyToInstall, not Failed", ApplicationUpdateServiceTests.TestFailedHandoffWriteLeavesStateReadyToInstall),
    ("A failed backend apply call resumes Manager-only services", ApplicationUpdateServiceTests.TestFailedBackendApplyCallTriggersRecovery),
    ("A profile-load failure after the shutdown gate rolls back cleanly", ApplicationUpdateServiceTests.TestProfileLoadFailureRollsBackAfterShutdownGateAcquired),
    ("A cancellation after the shutdown gate rolls back the same way as any other failure", ApplicationUpdateServiceTests.TestCancellationAfterShutdownGateRollsBackTheSameWayAsAnyOtherFailure),
    ("A failed backend apply call deletes the handoff file it already wrote", ApplicationUpdateServiceTests.TestFailedBackendApplyCallDeletesTheHandoffFile),
    ("Apply eligibility notifies when a blocking operation begins and ends", ApplicationUpdateServiceTests.TestApplyEligibilityNotifiesWhenABlockingOperationBeginsAndEnds),
    ("Apply eligibility notifies when the shutdown gate is canceled", ApplicationUpdateServiceTests.TestApplyEligibilityNotifiesWhenTheShutdownGateIsCanceled),
    ("Download uses the channel that produced the update, not a hardcoded default", VelopackUpdateBackendTests.TestDownloadUsesTheChannelThatProducedTheUpdateNotAHardcodedDefault),
    ("Download rejects a release not produced by this backend", VelopackUpdateBackendTests.TestDownloadRejectsAReleaseNotProducedByThisBackend),
    ("Concurrent apply attempts are rejected, not queued", ApplicationUpdateServiceTests.TestConcurrentApplyAttemptsAreRejected),
    ("Apply requires a state of ReadyToInstall", ApplicationUpdateServiceTests.TestApplyRequiresReadyToInstallState),
    ("Critical operation tracker: lease lifecycle", CriticalOperationTrackerTests.TestBeginTracksAnOperationUntilDisposed),
    ("Critical operation tracker: multiple concurrent leases", CriticalOperationTrackerTests.TestMultipleConcurrentLeasesAreAllTracked),
    ("Critical operation tracker: double-dispose is safe", CriticalOperationTrackerTests.TestDisposingALeaseTwiceIsSafe),
    ("Critical operation tracker: lease releases on exception", CriticalOperationTrackerTests.TestLeaseReleasesEvenWhenTheOperationThrows),
    ("Critical operation tracker: shutdown blocked while busy", CriticalOperationTrackerTests.TestTryBeginShutdownFailsWhileAnOperationIsActive),
    ("Critical operation tracker: shutdown succeeds when idle", CriticalOperationTrackerTests.TestTryBeginShutdownSucceedsWhenIdle),
    ("Critical operation tracker: no new operation after shutdown committed", CriticalOperationTrackerTests.TestNoNewCriticalOperationCanStartOnceShutdownIsCommitted),
    ("Critical operation tracker: cancel shutdown resumes operations", CriticalOperationTrackerTests.TestCancelShutdownAllowsOperationsToResume),
    ("Critical operation tracker: second shutdown attempt rejected", CriticalOperationTrackerTests.TestSecondShutdownAttemptIsRejectedAsAlreadyInProgress),
    ("Critical operation tracker: Changed fires on begin/end/shutdown-acquire/shutdown-cancel", CriticalOperationTrackerTests.TestChangedFiresOnBeginEndShutdownAcquireAndCancel),
    ("Critical operation tracker: Changed does not fire on a rejected shutdown attempt", CriticalOperationTrackerTests.TestChangedDoesNotFireOnARejectedShutdownAttempt),
    ("Server start registers as ServerStart", CriticalOperationWiringTests.TestServerStartRegistersAsServerStart),
    ("Server force-stop registers as ServerForceStop", CriticalOperationWiringTests.TestServerForceStopRegistersAsServerForceStop),
    ("Stopping an already-stopped server registers nothing", CriticalOperationWiringTests.TestServerStopOnAnAlreadyStoppedServerRegistersNothing),
    ("Backup registers as Backup", CriticalOperationWiringTests.TestBackupRegistersAsBackup),
    ("Restore registers as Restore and releases its lease on failure", CriticalOperationWiringTests.TestRestoreRegistersAsRestoreAndReleasesLeaseOnFailure),
    ("Settings save registers as SettingsWrite", CriticalOperationWiringTests.TestSettingsWriteRegistersAsSettingsWrite),
    ("Package export registers as PackageExport", CriticalOperationWiringTests.TestPackageExportRegistersAsPackageExport),
    ("Direct ProjectReference graph matches the accepted #19 topology for supported build contexts", ArchitectureGuardTests.TestDirectReferenceGraphMatchesAcceptedTopologyForSupportedContexts),
    ("Core has zero ProjectReferences", ArchitectureGuardTests.TestCoreHasNoProjectReferences),
    ("Contracts is Core-independent", ArchitectureGuardTests.TestContractsIsCoreIndependent),
    ("Contracts has no legacy Lan dependency", ArchitectureGuardTests.TestContractsHasNoLanDependency),
    ("No new v0.5 project references legacy Lan", ArchitectureGuardTests.TestNoNewV05ProjectReferencesLegacyLan),
    ("Client.Avalonia has no dependency path to Core/Host/Host.Persistence/Host-side Platform", ArchitectureGuardTests.TestClientAvaloniaHasNoHostSideDependencyPath),
    ("Client.Cli has no dependency path to Core/Host/Host.Persistence/Host-side Platform", ArchitectureGuardTests.TestClientCliHasNoHostSideDependencyPath),
    ("Host.Cli has no Contracts reference", ArchitectureGuardTests.TestHostCliHasNoContractsReference),
    ("Client.Avalonia and Client.Cli share Client.Platform.Contracts", ArchitectureGuardTests.TestOrdinaryClientsShareClientPlatformContracts),
    ("Windows and Linux implementations do not reference each other", ArchitectureGuardTests.TestWindowsAndLinuxImplementationsDoNotReferenceEachOther),
    ("Frozen WPF App still references legacy Lan unchanged", ArchitectureGuardTests.TestFrozenWpfAppStillReferencesLanUnchanged),
    ("Frozen legacy Lan has unchanged direct references", ArchitectureGuardTests.TestFrozenLegacyLanHasUnchangedDirectReferences),
    ("Every guarded project is built by the solution", ArchitectureGuardTests.TestEveryGuardedProjectIsBuiltBySolution),

    // #40 - Host persistence foundation
    ("Host database enables WAL journal mode", HostPersistenceTests.TestWalJournalModeEnabled),
    ("Host database enforces foreign keys on managed connections", HostPersistenceTests.TestForeignKeysEnforcedOnManagedConnections),
    ("Fresh database migrates to latest schema", HostPersistenceTests.TestFreshDatabaseMigratesToLatest),
    ("Prior-version fixture applies only the missing migration", HostPersistenceTests.TestPriorVersionFixtureAppliesOnlyTheMissingMigration),
    ("Already-current database performs no migration writes", HostPersistenceTests.TestAlreadyCurrentDatabasePerformsNoMigrationWrites),
    ("Failed migration rolls back fully and keeps the last committed version", HostPersistenceTests.TestFailedMigrationRollsBackFullyAndKeepsLastCommittedVersion),
    ("Unknown/newer schema version is refused", HostPersistenceTests.TestUnknownNewerSchemaVersionIsRejected),
    ("HostIdentity is a structural singleton", HostPersistenceTests.TestHostIdentityIsSingleton),
    ("HostId is stable across reopen", HostPersistenceTests.TestHostIdIsStableAcrossReopen),
    ("HostIdentity stores only an opaque credential reference", HostPersistenceTests.TestHostIdentityStoresOnlyAnOpaqueCredentialReference),
    ("Duplicate OsPrincipalRef is rejected", HostPersistenceTests.TestDuplicateOsPrincipalRefIsRejected),
    ("At most one active Owner is a database constraint", HostPersistenceTests.TestAtMostOneActiveOwnerIsADatabaseConstraint),
    ("Revoked principal cannot retain a verification key", HostPersistenceTests.TestRevokedPrincipalCannotRetainVerificationKey),
    ("Uninitialized Host has zero active Owners", HostPersistenceTests.TestUninitializedHostHasZeroActiveOwners),
    ("Initialized transition requires exactly one active Owner atomically", HostPersistenceTests.TestInitializedTransitionRequiresExactlyOneActiveOwnerAtomically),
    ("No observable Initialized state without exactly one Owner", HostPersistenceTests.TestNoObservableInitializedStateWithoutAnOwner),
    ("Owner initialization is transaction-composable and rolls back", HostPersistenceTests.TestOwnerInitializationIsTransactionComposableAndRollsBack),
    ("Active principal must have a verification key", HostPersistenceTests.TestActivePrincipalMustHaveAVerificationKey),
    ("Enrollment persists its creating Owner principal", HostPersistenceTests.TestEnrollmentPersistsItsCreatingOwnerPrincipal),
    ("Only one live initial-Owner enrollment may exist", HostPersistenceTests.TestOnlyOneLiveInitialOwnerEnrollmentMayExist),
    ("PendingCredentialReplacement captures the expected trust snapshot", HostPersistenceTests.TestPendingCredentialReplacementCapturesExpectedTrustSnapshot),
    ("TrustedManager state/credential combinations are constrained", HostPersistenceTests.TestTrustedManagerStateAndCredentialCombinationsAreConstrained),
    ("HostCredentialRotation supports the Prepared state", HostPersistenceTests.TestHostCredentialRotationSupportsPreparedState),
    ("TrustedManager credential history is retained per peer", HostPersistenceTests.TestTrustedManagerCredentialHistoryIsRetainedPerPeer),
    ("Owner recovery tickets require current-Owner snapshots", HostPersistenceTests.TestOwnerRecoveryTicketsRequireCurrentOwnerSnapshots),
    ("Re-home target snapshot tuple is coherent", HostPersistenceTests.TestRehomeTargetSnapshotTupleIsCoherent),
    ("Replacement expected-trust tuple mirrors TrustedManagers", HostPersistenceTests.TestReplacementExpectedTrustTupleMirrorsTrustedManagers),
    ("Schema has no raw secret persistence fields", HostPersistenceTests.TestSchemaHasNoRawSecretPersistenceFields),
    ("Verifier and public-key persistence is allowed", HostPersistenceTests.TestVerifierAndPublicKeyPersistenceIsAllowed),
    ("Transaction rollback discards all writes", HostPersistenceTests.TestTransactionRollbackDiscardsAllWrites),
    ("ServerInventory identity is Host-qualified", HostPersistenceTests.TestServerInventoryIsHostQualified),
    ("ServerInventory round-trips both ports", HostPersistenceTests.TestServerInventoryRoundTripsBothPorts),
    ("HostCapabilityGrant requires exactly one TargetHostId", HostPersistenceTests.TestHostCapabilityGrantRequiresExactlyOneTargetHostId),
    ("Host and server grant types are structurally distinct", HostPersistenceTests.TestGrantTypesAreStructurallyDistinct),
    ("Grant delegation provenance is single-parent", HostPersistenceTests.TestGrantDelegationProvenanceIsSingleParent),
    ("TrustedManager tombstone clears its pinned credential", HostPersistenceTests.TestTrustedManagerTombstoneClearsPinnedCredential),
    ("PendingCredentialReplacement carries no grant authority", HostPersistenceTests.TestPendingCredentialReplacementCarriesNoGrantAuthority),
    ("OperationRecord requires an explicit discriminated target", HostPersistenceTests.TestOperationRecordRequiresExplicitDiscriminatedTarget),
    ("OperationLock scope is independent of target and requires its owning record", HostPersistenceTests.TestOperationLockScopeIsIndependentOfTargetAndRequiresOwningRecord),
    ("RecoveryDisposition is persistable", HostPersistenceTests.TestRecoveryDispositionIsPersistable),
    ("ConfigurationRevisions support revision tokens", HostPersistenceTests.TestConfigurationRevisionsSupportRevisionTokens),
    ("AuditEvents support same-transaction offline-recovery writes", HostPersistenceTests.TestAuditEventsSupportSameTransactionOfflineRecoveryWrites),
    ("WAL-safe snapshot captures uncheckpointed data and passes integrity check", HostPersistenceTests.TestSnapshotCapturesUncheckpointedWalDataAndPassesIntegrityCheck),
    ("Raw file copy under WAL is demonstrably unsafe", HostPersistenceTests.TestRawFileCopyUnderWalIsUnsafeAndIsNotUsed),

    // #40 - machine-wide exclusivity lock (cross-process)
    ("Cross-process exclusion, release, and re-acquisition sequence", HostExclusivityLockTests.TestCrossProcessExclusionAndReleaseSequence),
    ("Abandoned lock is reacquirable without requiring AbandonedMutexException", HostExclusivityLockTests.TestAbandonedLockIsReacquirableWithoutRequiringAbandonedMutexException),
    ("Exclusivity lease survives async thread hops", HostExclusivityLockTests.TestLeaseSurvivesAsyncThreadHops),
    ("Second writer is refused immediately", HostExclusivityLockTests.TestSecondWriterIsRefusedImmediately),
    ("Exclusivity lock Dispose is idempotent", HostExclusivityLockTests.TestDisposeIsIdempotent)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL  {test.Name}");
        Console.WriteLine(ex);
    }
}

Console.WriteLine();
Console.WriteLine($"{tests.Count - failures}/{tests.Count} self-tests passed.");
return failures == 0 ? 0 : 1;

static Task TestConfigParser()
{
    const string text = "[/Script/Pal.PalGameWorldSettings]\r\nOptionSettings=(ServerName=\"Friends, Pals & Chaos\",DenyTechnologyList=(\"PALBOX\",\"RepairBench\"),CrossplayPlatforms=(Steam,Xbox,PS5,Mac),ExpRate=1.500000,UnknownFutureSetting=\"a,b,c\")\r\n";
    var doc = PalworldConfigParser.Parse(text);
    Equal("\"Friends, Pals & Chaos\"", doc.Get("ServerName"));
    Equal("(\"PALBOX\",\"RepairBench\")", doc.Get("DenyTechnologyList"));
    Equal("(Steam,Xbox,PS5,Mac)", doc.Get("CrossplayPlatforms"));
    Equal("\"a,b,c\"", doc.Get("UnknownFutureSetting"));
    return Task.CompletedTask;
}

static Task TestUnknownRoundTrip()
{
    const string text = "; retained comment\n[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(Known=True,FutureThing=(One,Two),StringValue=\"hello, world\")\n; trailing comment\n";
    var doc = PalworldConfigParser.Parse(text);
    doc.Set("Known", "False");
    var serialized = doc.Serialize();
    True(serialized.Contains("; retained comment"), "prefix comment not retained");
    True(serialized.Contains("; trailing comment"), "suffix comment not retained");
    var reparsed = PalworldConfigParser.Parse(serialized);
    Equal("False", reparsed.Get("Known"));
    Equal("(One,Two)", reparsed.Get("FutureThing"));
    Equal("\"hello, world\"", reparsed.Get("StringValue"));
    return Task.CompletedTask;
}

static async Task TestNonDestructiveCopy()
{
    var root = Path.Combine(Path.GetTempPath(), "psm-selftest-" + Guid.NewGuid().ToString("N"));
    var source = Path.Combine(root, "source");
    var dest = Path.Combine(root, "dest");
    try
    {
        Directory.CreateDirectory(Path.Combine(source, "SaveGames", "0", "ABC"));
        await File.WriteAllTextAsync(Path.Combine(source, "SaveGames", "0", "ABC", "Level.sav"), "world-data");
        await File.WriteAllTextAsync(Path.Combine(source, "PalWorldSettings.ini"), "settings");
        var before = await DirectoryHashService.HashTreeAsync(source);
        FileCopyService.CopyDirectory(source, dest);
        var after = await DirectoryHashService.HashTreeAsync(source);
        True(DirectoryHashService.Equivalent(before, after, out var difference), difference);
        var copied = await DirectoryHashService.HashTreeAsync(dest);
        True(DirectoryHashService.Equivalent(before, copied, out difference), "copy mismatch: " + difference);
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}

static async Task TestProfileRegistry()
{
    var root = Path.Combine(Path.GetTempPath(), "psm-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        var paths = new AppPaths(root);
        var logger = new FileLogger(paths);
        var registry = new ProfileRegistry(paths, logger);
        var profile = new ServerProfile { Name = "Test Server", InstallPath = Path.Combine(root, "server") };
        await registry.AddAsync(profile);
        var loaded = await registry.LoadAsync();
        Equal(1, loaded.Count);
        Equal(profile.Id, loaded[0].Id);
        Equal("Test Server", loaded[0].Name);
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}

static async Task TestDiscovery()
{
    var root = Path.Combine(Path.GetTempPath(), "psm-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        var legacy = Path.Combine(root, "legacy", "PalServer");
        Directory.CreateDirectory(Path.Combine(legacy, "Pal", "Saved", "Config", "WindowsServer"));
        Directory.CreateDirectory(Path.Combine(legacy, "Pal", "Saved", "SaveGames", "0", "ABC"));
        await File.WriteAllTextAsync(Path.Combine(legacy, "PalServer.exe"), "placeholder");
        await File.WriteAllTextAsync(Path.Combine(legacy, "DefaultPalWorldSettings.ini"), "[/Script/Pal.PalGameWorldSettings]\nOptionSettings=()\n");
        await File.WriteAllTextAsync(Path.Combine(legacy, "Pal", "Saved", "Config", "WindowsServer", "PalWorldSettings.ini"), "[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(ServerName=\"Imported Test\")\n");
        await File.WriteAllTextAsync(Path.Combine(legacy, "Pal", "Saved", "SaveGames", "0", "ABC", "Level.sav"), "data");

        var paths = new AppPaths(Path.Combine(root, "manager"));
        var logger = new FileLogger(paths);
        var registry = new ProfileRegistry(paths, logger);
        var locator = new SteamLocator(paths, logger);
        var discovery = new ServerDiscoveryService(locator, registry);
        var candidate = discovery.Analyze(legacy, await registry.LoadAsync());
        Equal(ExistingServerClassification.ValidExistingServer, candidate.Classification);
        Equal("Imported Test", candidate.DisplayName);
        True(candidate.HasSaveData, "save not detected");
        True(candidate.HasSettings, "settings not detected");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}

static async Task TestStructuredLogging()
{
    var root = Path.Combine(Path.GetTempPath(), "psm-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        var paths = new AppPaths(root);
        var logger = new FileLogger(paths);
        var serverId = Guid.NewGuid();
        using (logger.BeginOperation("SelfTestOperation", serverId, "Logging Test"))
        {
            logger.Info("inside operation");
            logger.Warning("warning sample");
            logger.Error("error sample", new InvalidOperationException("synthetic failure"));
        }

        var text = await File.ReadAllTextAsync(logger.CurrentLogFile);
        True(text.Contains($"session={logger.SessionId}"), "session id missing from log");
        True(text.Contains("BEGIN operation 'SelfTestOperation'"), "operation begin missing");
        True(text.Contains("END operation 'SelfTestOperation'"), "operation end missing");
        True(text.Contains(serverId.ToString("D")), "server id missing from operation context");
        True(text.Contains("synthetic failure"), "exception details missing");
        var perServerLog = Path.Combine(paths.LogsRoot, "servers", $"server-{serverId:D}.log");
        True(File.Exists(perServerLog), "per-server correlated log was not created");
        var perServerText = await File.ReadAllTextAsync(perServerLog);
        True(perServerText.Contains("SelfTestOperation"), "per-server log is missing correlated operation content");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}


static Task TestSteamCmdRecoveryClassification()
{
    var code7 = new SteamCmdException(7);
    var code8 = new SteamCmdException(8);
    Equal(7, code7.ExitCode);
    True(code7.SuggestSteamClientRecovery, "exit code 7 should suggest Steam client recovery");
    True(!code8.SuggestSteamClientRecovery, "unrelated exit codes should not be mislabeled as the field-tested code-7 recovery case");
    return Task.CompletedTask;
}


static Task TestServerLifetimeExitResult()
{
    var result = new ServerProcessLifetimeEndedEventArgs
    {
        ServerId = Guid.NewGuid(),
        ServerName = "Lifetime Test",
        ExpectedStop = false,
        ProcessExits =
        [
            new ServerProcessExitInfo(100, "PalServer", 0),
            new ServerProcessExitInfo(101, "PalServer-Win64-Shipping-Cmd", 42)
        ],
        Message = "synthetic lifetime result"
    };

    True(result.HasNonZeroExitCode, "non-zero shipping exit should classify the lifetime as an error");
    Equal(42, result.PrimaryExitCode);
    return Task.CompletedTask;
}

static async Task TestDiagnosticBundle()
{
    var root = Path.Combine(Path.GetTempPath(), "psm-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        var paths = new AppPaths(Path.Combine(root, "manager"));
        var logger = new FileLogger(paths);
        var profile = new ServerProfile
        {
            Name = "Diagnostic Test",
            InstallPath = Path.Combine(root, "server", "PalServer")
        };
        Directory.CreateDirectory(Path.GetDirectoryName(profile.SettingsPath)!);
        Directory.CreateDirectory(Path.Combine(profile.SavedPath, "Logs"));
        Directory.CreateDirectory(Path.Combine(profile.SavedPath, "SaveGames", "0", "ABC"));
        await File.WriteAllTextAsync(profile.SettingsPath,
            "[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(ServerName=\"Diag\",AdminPassword=\"super-secret-admin\",ServerPassword=\"super-secret-server\",ExpRate=1.0)\n");
        await File.WriteAllTextAsync(Path.Combine(profile.SavedPath, "Logs", "PalServer.json"), "{\"event\":\"server log sample\"}\n");
        await File.WriteAllTextAsync(Path.Combine(profile.SavedPath, "SaveGames", "0", "ABC", "Level.sav"), "must never be exported in diagnostics");
        logger.Info("diagnostic manager log sample");

        var diagnostics = new DiagnosticBundleService(paths, logger);
        var output = Path.Combine(root, "diagnostics.zip");
        await diagnostics.CreateAsync(output, profile);

        using var zip = ZipFile.OpenRead(output);
        True(zip.Entries.Any(x => x.FullName == "server/PalWorldSettings.sanitized.ini"), "sanitized settings missing");
        True(zip.Entries.Any(x => x.FullName.StartsWith("manager-logs/")), "manager logs missing");
        True(zip.Entries.Any(x => x.FullName == "server/logs/PalServer.json"), "JSON server log missing");
        True(!zip.Entries.Any(x => x.FullName.EndsWith(".sav", StringComparison.OrdinalIgnoreCase)), "diagnostic bundle contains a save file");

        var settingsEntry = zip.GetEntry("server/PalWorldSettings.sanitized.ini")!;
        using var reader = new StreamReader(settingsEntry.Open());
        var settings = await reader.ReadToEndAsync();
        True(!settings.Contains("super-secret-admin"), "admin password leaked into diagnostic bundle");
        True(!settings.Contains("super-secret-server"), "server password leaked into diagnostic bundle");
        True(settings.Contains("***REDACTED***"), "redaction marker missing");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}

static void True(bool condition, string message = "assertion failed")
{
    if (!condition) throw new Exception(message);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected '{expected}', got '{actual}'.");
}
