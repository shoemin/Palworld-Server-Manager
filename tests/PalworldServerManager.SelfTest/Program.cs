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
    if (args is ["--peer-process-host", var peerProcessConfig])
        return await WindowsPeerProcessFixture.RunChild(peerProcessConfig);
    if (args is ["--operation-lifecycle-child", var opRoot, var opHost, var opId, var opProfile, var opScope, var opMode, var opMutex])
        return OperationCrashTests.RunChild(opRoot, opHost, opId, opProfile, opScope, opMode, opMutex);
    if (args is ["--recovery-pull-probe"])
    {
        await RecoverySenderTests.PullActualIndependentRecoveryAndFreshConnections();
        await RecoverySenderTests.PullReceiptValidationIsReadOnlyAndExact();
        await RecoverySenderTests.PullLostStagesRetryOriginalReceiptAfterListenerRestart();
        await RecoverySenderTests.PullChangedAuthorityAndSupersededReceiptNeverAttest();
        await RecoverySenderTests.PullMalformedOffersAndMismatchAudit();
        await RecoverySenderTests.PullMalformedConfirmationAndPostCommitChangeRefuse();
        await RecoverySenderTests.PullNegotiationNoPendingAndInputBounds();
        await RecoverySenderTests.PullGenerationStopDrainsSecondNativeCallback();
        await RecoverySenderTests.PullSharedDeadlineSpansBothConnections();
        await RecoverySenderTests.PullConcurrentClientsStayIdempotent();
        Console.WriteLine("PASS ten recovery pull scenarios."); return 0;
    }
    if (args is ["--recovery-offer-probe"])
    {
        await RecoverySenderTests.OfferWireShapesAndHistory();
        await RecoverySenderTests.OfferActualReceiptFreshConfirmationAndMutualRecovery();
        await RecoverySenderTests.OfferPositiveExactAttestationOnlyAndConcurrentWinner();
        await RecoverySenderTests.OfferFeatureNegotiationAndNoPending();
        await RecoverySenderTests.OfferOriginalSessionCannotRecaptureRecoveryIncarnation();
        await RecoverySenderTests.OfferCurrentAuthorityAndLateAuditRollback();
        await RecoverySenderTests.OfferOwnedChannelAndCancellation();
        await RecoverySenderTests.OfferStagedKeyCannotConfirmApprovedOldKey();
        Console.WriteLine("PASS eight recovery offer scenarios."); return 0;
    }
    if (args is ["--recovery-generation-probe"])
    {
        await RecoverySenderTests.GenerationComposedSenderAndClosedAdmission();
        await RecoverySenderTests.GenerationStopDrainsActualRecoveryCallback();
        await RecoverySenderTests.GenerationInterruptedReplyReopensExactApproval();
        await RecoverySenderTests.GenerationRecoveryRequiresConfiguredServingLifetime();
        Console.WriteLine("PASS four recovery generation scenarios."); return 0;
    }
    if (args is ["--recovery-sender-probe"])
    {
        await RecoverySenderTests.ActualMutualCompletionAndFreshAuthority();
        await RecoverySenderTests.ActualOwnRecoveryUsesCurrentKeyAndHistoricalApproval();
        await RecoverySenderTests.UnapprovedNewOwnKeyAndOldLocalKeyRefuse();
        await RecoverySenderTests.LostReplyRetriesExactDurableApproval();
        await RecoverySenderTests.ActualSimultaneousCompletionRequiresFreshIncarnation();
        await RecoverySenderTests.ChangedLocalAuthorityNeverConfirms();
        await RecoverySenderTests.MalformedAndMismatchRepliesNeverConfirm();
        await RecoverySenderTests.NegotiationAndNoPendingAreBounded();
        await RecoverySenderTests.CancellationDrainsActualNativeCallback();
        await RecoverySenderTests.ActualDeadlineDisposesHeldResponse();
        await RecoverySenderTests.ConcurrentNativeSendersStayIdempotent();
        Console.WriteLine("PASS eleven native recovery sender scenarios."); return 0;
    }
    if (args is ["--recovery-confirmation-probe"])
    {
        await PeerTrustRevocationTests.ConfirmationChangesOnlyMarkerAndActualAudit();
        await PeerTrustRevocationTests.ConfirmationAbsentAndDuplicateAreReadOnly();
        await PeerTrustRevocationTests.ConfirmationUsesCurrentLocalKeyAfterOfflineRecovery();
        await PeerTrustRevocationTests.ConfirmationRechecksIncomingReceiptIncarnation();
        await PeerTrustRevocationTests.ConfirmationRefusesStaleProofAndUnapprovedMarkers();
        await PeerTrustRevocationTests.ConfirmationNeverSubstitutesStagedPeerKey();
        await PeerTrustRevocationTests.ConfirmationLateEffectsAndAuditRollback();
        await PeerTrustRevocationTests.ConfirmationConcurrencyAndCancellation();
        Console.WriteLine("PASS eight exact recovery confirmation scenarios."); return 0;
    }
    if (args is ["--recovery-rpc-probe"])
    {
        await PeerRecoveryRpcTests.WireAndImmutableHistory();
        await PeerRecoveryRpcTests.BothRecoveryExactReceiptAndLostReplyDuplicate();
        await PeerRecoveryRpcTests.RecoverySessionCannotBecomeOrdinary();
        await PeerRecoveryRpcTests.ProtocolBoundsAndRecipientRefusals();
        await PeerRecoveryRpcTests.UnknownRevokedAndBoundRecoveryKeysFailTls();
        await PeerRecoveryRpcTests.HeldKeysAndIncarnationCannotOutliveState();
        await PeerRecoveryRpcTests.OldAndPendingKeyDoNotPromoteDuringRecovery();
        await PeerRecoveryRpcTests.AuditFaultRollsBackAndErrorsAreBounded();
        await PeerRecoveryRpcTests.OwnedAdapterCancellationAndLifetime();
        Console.WriteLine("PASS nine fixed recovery RPC scenarios and immutable protocol history."); return 0;
    }
    if (args is ["--offline-recovery-continuity-probe"])
    {
        await PeerTrustRevocationTests.OfflineRecoveryAuditDeletionRollsBack();
        await PeerTrustRevocationTests.OfflineRecoveryCarriesExactApprovalThroughRepeatedRecovery();
        await PeerTrustRevocationTests.OfflineRecoveryNeverRevivesIneligibleMarkers();
        await PeerTrustRevocationTests.OfflineRecoveryContinuityStillCancelsOnRevokeAndReplacement();
        await PeerTrustRevocationTests.OfflineRecoveryLateEffectsRollBackWholeWriter();
        await HostTrustReconciliationTests.RecoveryMetadataAndRollback();
        Console.WriteLine("PASS five offline recovery continuity scenarios and existing recovery metadata regression."); return 0;
    }
    if (args is ["--offline-recovery-regression-probe"])
    {
        await PeerTrustRevocationTests.OfflineRecoveryAuditDeletionRollsBack();
        Console.WriteLine("PASS offline recovery final-audit rollback regression."); return 0;
    }
    if (args is ["--recovery-receipt-probe"])
    {
        await PeerTrustRevocationTests.RecoveryReceiptClearsExactKeyWithoutNewGrants();
        await PeerTrustRevocationTests.RecoveryReceiptDuplicateAndNoOpAreBounded();
        await PeerTrustRevocationTests.RecoveryReceiptMismatchAndSecondRecoveryCannotUnlock();
        await PeerTrustRevocationTests.RecoveryReceiptRefusesChangedProofAndRelationship();
        await PeerTrustRevocationTests.RecoveryReceiptPreservesOldAndStagedNewRotation();
        await PeerTrustRevocationTests.RecoveryReceiptCarriesOnlyExactOutgoingApproval();
        await PeerTrustRevocationTests.RecoveryReceiptLateEffectsAndAuditsRollback();
        await PeerTrustRevocationTests.RecoveryReceiptConcurrencyAndCancellation();
        await PeerTrustRevocationTests.RecoveryReceiptSchemaAddsNoAuthority();
        Console.WriteLine("PASS nine exact-key recovery receipt scenarios."); return 0;
    }
    if (args is ["--owner-replacement-probe"])
    {
        await PeerTrustRevocationTests.OwnerReplacementAppliesCurrentDefaultsAndExactForest();
        await PeerTrustRevocationTests.OwnerReplacementHandlesRevokedPeerBoundAndBenignRotation();
        await PeerTrustRevocationTests.OwnerReplacementRetainsRecoveryAndDeniesOrdinaryAuthority();
        await PeerTrustRevocationTests.OwnerReplacementDeniesStaleAndUnprovenRequests();
        await PeerTrustRevocationTests.OwnerReplacementRejectsFreshStagedCandidate();
        await PeerTrustRevocationTests.OwnerReplacementRetriesAndSupersedesWithoutRevival();
        await PeerTrustRevocationTests.OwnerReplacementLateFaultsRollback();
        await PeerTrustRevocationTests.OwnerReplacementConcurrencyAndCancellation();
        await PeerTrustRevocationTests.ReplacementCompletionSchemaAddsNoAuthority();
        Console.WriteLine("PASS nine local Owner replacement scenarios."); return 0;
    }
    if (args is ["--replacement-candidate-probe"])
    {
        await ReplacementCandidateTests.FreshEvidenceIsDurableAndIdempotent();
        await ReplacementCandidateTests.TrustAndStagedRotationAbaPermanentlyInvalidate();
        await ReplacementCandidateTests.LocalCredentialAbaPermanentlyInvalidates();
        await ReplacementCandidateTests.MissingOrMismatchedProofCannotBeReused();
        await ReplacementCandidateTests.CanonicalRevocationPreservesItsAtomicTimestamp();
        await ReplacementCandidateTests.LegacyRequestsNeverGainFreshEvidence();
        await ReplacementCandidateTests.UnrelatedAndApprovedHistoryRemainIntact();
        await ReplacementCandidateTests.LateCandidateAuditAndAuthorityFaultsRollback();
        Console.WriteLine("PASS eight replacement candidate provenance scenarios."); return 0;
    }
    if (args is ["--unpair-coordinator-probe"])
    {
        await LiveUnpairConnectionTests.LocalCommitReturnsBeforeHeldNotification();
        await LiveUnpairConnectionTests.MissingAndPreparingConnectionsNeverDelayCommit();
        await LiveUnpairConnectionTests.DeniedStaleCanceledCallsLeaveReadyConnectionUnused();
        await LiveUnpairConnectionTests.RemoteFacadeUsesSameNotificationWithoutEcho();
        await LiveUnpairConnectionTests.CoordinatorRejectsCrossHostWiring();
        await UnpairCoordinatorLifetimeTests.CapacityAndRemovedWorkersDrainActualCleanup();
        await UnpairCoordinatorLifetimeTests.RetentionExpiryClosesPreparationWithoutNotice();
        await UnpairCoordinatorLifetimeTests.StopCallbackFailureStillDrainsWorkers();
        await AuthenticatedPermissionDispatchTests.BothDispatchersRetireNotReadyNotifications();
        await HostNetworkGenerationTests.RegisteredUnpairPreparationStopsWithGeneration();
        Console.WriteLine("PASS ten unpair coordinator and facade/lifetime scenarios."); return 0;
    }
    if (args is ["--live-unpair-probe"])
    {
        await PeerTrustRevocationTests.CommittedUnpairGuardRequiresExactTransition();
        await LiveUnpairConnectionTests.ActualScopedDeliveryAndSingleAttempt();
        await LiveUnpairConnectionTests.UncommittedOrLaterTransitionNeverSends();
        await LiveUnpairConnectionTests.LostActualTransportCannotAuthenticateAgain();
        await LiveUnpairConnectionTests.HeldResponseDisposalDrainsSend();
        await LiveUnpairConnectionTests.PreparationRequiresActiveAndActualProof();
        await HostNetworkGenerationTests.PreparedUnpairConnectionBelongsToGeneration();
        await PeerRotationReceiptRpcTests.PreparationFeatureFailureRetainsObservedRotation();
        Console.WriteLine("PASS eight live unpair guard/connection/generation/rotation scenarios."); return 0;
    }
    if (args is ["--unpair-rpc-probe"])
    {
        await PeerUnpairRpcTests.WireAndFeatureAreClosed();
        await PeerUnpairRpcTests.ActualReceiptDuplicatesAndStaleHandshake();
        await PeerUnpairRpcTests.ProtocolRecipientAndNonActiveRefusals();
        await PeerUnpairRpcTests.ActualCurrentKeyAndLaterRelationshipRefuse();
        await PeerUnpairRpcTests.ActualAuditFaultIsAtomicAndBounded();
        await PeerUnpairRpcTests.AdapterConnectionLifetimeAndRuntimeRefusals();
        await ProtocolTests.SchemaEvolution();
        Console.WriteLine("PASS six negotiated unpair RPC scenarios and schema history."); return 0;
    }
    if (args is ["--reciprocal-unpair-probe"])
    {
        await PeerTrustRevocationTests.ReciprocalNoticeIsSelfOnlyAndAtomic();
        await PeerTrustRevocationTests.ReciprocalDuplicatePreservesNewPendingIntent();
        await PeerTrustRevocationTests.ReciprocalReceiptNeverRestoresOrdinaryOrLaterTrust();
        await PeerTrustRevocationTests.ReciprocalReceiptAndAuditFaultsRollback();
        await PeerTrustRevocationTests.ReciprocalConcurrencyAndLateCancellation();
        await PeerTrustRevocationTests.ReciprocalSchemaUpgradeAndIncarnationCascade();
        await PeerTrustRevocationTests.ReciprocalNonActiveAndAbsentEvidenceRefuse();
        Console.WriteLine("PASS seven reciprocal unpair receipt scenarios."); return 0;
    }
    if (args is ["--remote-trust-revocation-probe"])
    {
        await PeerTrustRevocationTests.RemoteAdministratorTargetsOnlyItsSelectedPeer();
        await PeerTrustRevocationTests.RemoteSelfRevocationCannotReuseItsOldProof();
        await PeerTrustRevocationTests.RemoteProofAndExactCapabilityRefusals();
        await PeerTrustRevocationTests.RemoteLateIdentityAndAuditEffectsRollback();
        await PeerTrustRevocationTests.RemoteRevocationMayRemoveItsOwnDelegatedCapability();
        await AuthenticatedPermissionDispatchTests.LocalRevocationDispatchBindsActorAndLifetime();
        await AuthenticatedPermissionDispatchTests.RemoteRevocationDispatchBindsOriginalProof();
        await AuthenticatedPermissionDispatchTests.RevocationAfterStagedPromotionNeedsFreshRevision();
        Console.WriteLine("PASS eight remote revocation and dispatch scenarios."); return 0;
    }
    if (args is ["--local-trust-revocation-probe"])
    {
        await PeerTrustRevocationTests.AtomicForestsTombstoneAndAudit();
        await PeerTrustRevocationTests.LocalCapabilityAndIdentityBoundaries();
        await PeerTrustRevocationTests.IdempotenceAndFreshPairingGate();
        await PeerTrustRevocationTests.StaleRequestAndCancellationRollback();
        await PeerTrustRevocationTests.EveryLateEffectAndAuditFailureRollsBack();
        await PeerTrustRevocationTests.ConcurrentRevocationsHaveOneWinner();
        await PeerTrustRevocationTests.HistoricalInvalidationRemainsPermanent();
        await PeerTrustRevocationTests.PeerBoundAndRecoveryStatesAreRevocable();
        Console.WriteLine("PASS eight local trust revocation scenarios."); return 0;
    }
    if (args is ["--operation-cross-host-probe"])
    {
        await OperationCrossHostTests.DestinationOwnershipVisibilityAndDisconnectAcrossScopes();
        Console.WriteLine("PASS cross-Host operation ownership and observation."); return 0;
    }
    if (args is ["--host-operation-lifetime-probe"])
    {
        await HostOperationLifetimeTests.BootstrapThenConcurrentReadyUsesOneRuntime();
        await HostOperationLifetimeTests.EmptyProductionRegistryPreservesEveryTarget();
        await HostOperationLifetimeTests.FailedInitializationNeverRetriesInSameOwner();
        await HostOperationLifetimeTests.ShutdownRacingReadyDrainsActualWork();
        await HostOperationLifetimeTests.EnclosingLeaseOutlivesWorkerDrain();
        Console.WriteLine("PASS Host operation lifetime ownership."); return 0;
    }
    if (args is ["--operation-activity-probe"])
    {
        await OperationActivityTests.TargetsScopesAndIndependentReaders();
        await OperationActivityTests.MalformedAndUnknownWireValuesRefuse();
        await OperationActivityTests.RuntimeStatusChangesWithoutDurableRevision();
        await OperationActivityTests.InconsistentHistoryAndOrphanLocksRemainVisible();
        await OperationActivityTests.NegotiationAndEveryObservationValue();
        await ProtocolTests.SchemaEvolution();
        Console.WriteLine("PASS operation Activity contracts."); return 0;
    }
    if (args is ["--host-operation-recovery-probe"])
    {
        await HostOperationRecoveryTests.PreparationRequiresExactPolicyAndKeepsIdentityAndLock();
        await HostOperationRecoveryTests.PreparationFaultsRollBackAndRetry();
        await HostOperationRecoveryTests.StartupUsesOnlyExplicitHandlersOnBothTargets();
        await HostOperationRecoveryTests.DiscardCleansBeforeReleasingEitherScope();
        await HostOperationRecoveryTests.MissingChangedManualAndDamagedStateNeverDispatch();
        await HostOperationRecoveryTests.ConcurrentInitializationAndFailureNeverLoop();
        await HostOperationRecoveryTests.RecoveryShutdownDrainsAndKeepsPreparedState();
        await HostOperationRecoveryTests.LateAuthorityRefusalSkipsOnlyThatWorker();
        await OperationCrashTests.RealProcessTerminationPreservesAtomicPairs();
        Console.WriteLine("PASS explicit Host startup operation recovery."); return 0;
    }
    if (args is ["--host-operation-worker-probe"])
    {
        await HostOperationWorkerTests.DisconnectAndWaitCancellationDoNotCancelWork();
        await HostOperationWorkerTests.ShutdownDrainsWorkersAndRetainsUnfinishedState();
        await HostOperationWorkerTests.FailedAndUnfinishedWorkersNeverReleaseLocks();
        await HostOperationWorkerTests.FailedAndContendingAdmissionsDispatchExactlyOnce();
        await HostOperationWorkerTests.WorkerDoesNotInheritAmbientRequestContext();
        await HostOperationWorkerTests.StartupInspectsEveryTargetWithoutImplicitDispatch();
        await HostOperationWorkerTests.StaleExternalTransitionDoesNotOverwriteOrRelease();
        await HostOperationWorkerTests.ThrowingShutdownCallbackStillDrains();
        await HostOperationWorkerTests.ConcurrentShutdownCannotLoseAnAdmittedWorker();
        Console.WriteLine("PASS Host-owned operation workers."); return 0;
    }
    if (args is ["--operation-lifecycle-probe"])
    {
        await OperationLifecycleTests.FingerprintsAndQualifiedTargets();
        await OperationLifecycleTests.ConflictMatrixAndReadOnlyVisibility();
        await OperationLifecycleTests.ContendingAdmissionAndStaleTransitions();
        await OperationLifecycleTests.FaultMatricesRollBackAndRetry();
        await OperationLifecycleTests.AuthorityCancellationAndClosedInputs();
        await OperationLifecycleTests.HistoricalAndChangedDefinitionsRequireRecovery();
        await OperationLifecycleTests.RevisionIntegrityAndCollateralMutation();
        await OperationCrashTests.RealProcessTerminationPreservesAtomicPairs();
        Console.WriteLine("PASS atomic durable operation lifecycle."); return 0;
    }
    if (args is ["--configuration-revision-probe"])
    {
        await ConfigurationRevisionTests.QualifiedResourcesRemainIndependentAfterReopen();
        await ConfigurationRevisionTests.ConcurrentWritersRejectStaleBeforeAction();
        await ConfigurationRevisionTests.ActionAndFinalValidationFailuresRollBack();
        await ConfigurationRevisionTests.RevisionTriggerFaultsCannotCommitPartialData();
        await ConfigurationRevisionTests.InvalidStorageHostAndCancellationNeverRunAction();
        await ConfigurationRevisionTests.ReadersSeeCommittedStateAndLateHostChangesRollBack();
        Console.WriteLine("PASS transactional resource revisions."); return 0;
    }
    if (args is ["--operation-definition-probe"])
    {
        await OperationDefinitionTests.FixedConflictHierarchyAndIndependentTargets();
        await OperationDefinitionTests.ClosedIdentityAndLockInputs();
        await OperationDefinitionTests.ExplicitPerKindRecoveryAndTransitions();
        await OperationDefinitionTests.InvalidDefinitionsAndCallerMutationCannotSupplyFallbacks();
        Console.WriteLine("PASS explicit operation definitions."); return 0;
    }
    if (args is ["--permission-success-audit-probe"])
    {
        await PermissionSuccessAuditTests.RemovedAuditRollsBackGrant();
        await PermissionSuccessAuditTests.EverySuccessfulPathChecksEveryAuditField();
        await PermissionSuccessAuditTests.LaterBatchAndEnclosingAuditCannotEraseEarlierRows();
        await PermissionSuccessAuditTests.ConcurrentSuccessfulBatchesKeepIndependentAuditGuards();
        Console.WriteLine("PASS successful permission audit integrity."); return 0;
    }
    if (args is ["--authenticated-permission-dispatch-probe"])
    {
        await AuthenticatedPermissionDispatchTests.LocalSignaturesFeedCanonicalOwnerAndDelegationActions();
        await AuthenticatedPermissionDispatchTests.PeerActionsUseRealPeerAndCreatorKeepsIndependentUserCeiling();
        await AuthenticatedPermissionDispatchTests.LocalChannelNegotiationAndCurrentIdentityGateCallbacks();
        await AuthenticatedPermissionDispatchTests.PeerChannelNegotiationAndOriginalProofGateCallbacks();
        await AuthenticatedPermissionDispatchTests.CallLifetimeCancellationAndRuntimeAssociationAreBound();
        await AuthenticatedPermissionDispatchTests.GuardedPromotionDoesNotBecomePermissionOrLocalUserAuthority();
        Console.WriteLine("PASS authenticated Host permission dispatch."); return 0;
    }
    if (args is ["--bound-peer-observation-probe"])
    {
        await BoundPeerObservationTests.CurrentLapsedAndPendingPreserveIdentityAndExactEffects();
        await BoundPeerObservationTests.WrongConnectionAndInactiveTrustNeverObserve();
        await BoundPeerObservationTests.AuditHistoryAndLateProofMutationsRollBack();
        await BoundPeerObservationTests.CancellationConcurrencyAndMissingRevisionFailClosed();
        Console.WriteLine("PASS original connection bound peer observation."); return 0;
    }
    if (args is ["--capability-use-probe"])
    {
        await CapabilityUseTests.EveryTypedCapabilityUsesOnlyItsExactGrant();
        await CapabilityUseTests.QualifiedTargetsAndRevocationInvalidateEarlierObservations();
        await CapabilityUseTests.TwoHostCeilingsAreIndependentForHostAndServer();
        await CapabilityUseTests.EveryEntryRequiresCurrentProofAndClosedInputs();
        await CapabilityUseTests.UseDenialsRecordRealActorTargetAndSurviveContention();
        await CapabilityUseTests.UseAuditFaultsAndLateProofChangesRollBack();
        Console.WriteLine("PASS authenticated current capability use."); return 0;
    }
    if (args is ["--permission-denial-probe"])
    {
        await PermissionDenialAuditTests.EveryPreWritePathRecordsActualActorWithoutEffects();
        await PermissionDenialAuditTests.AuthenticationMalformedStaleAndCancellationAreNotPolicyDenials();
        await PermissionDenialAuditTests.DenialAuditFailureRemovalMutationAndIdentityRaceRollBack();
        await PermissionDenialAuditTests.PresetDenialAndCallbackFailureNeverCommitPartialWork();
        await PermissionDenialAuditTests.ConcurrentDenialsKeepRevisionAndSuccessfulGrantsSeparate();
        await PermissionDenialAuditTests.LateCancellationAndSuccessfulAuditFailureDoNotBecomeDenials();
        Console.WriteLine("PASS durable pre-write permission denial audit."); return 0;
    }
    if (args is ["--remote-grants-probe"])
    {
        await RemoteGrantPolicyTests.ExactDelegationPersistsRealPeerAndProvenance();
        await RemoteGrantPolicyTests.RootsScopeAndRightsCannotBeManufactured();
        await RemoteGrantPolicyTests.AllEntryPointsRequireCurrentTransportAndRevision();
        await RemoteGrantPolicyTests.LateAuditMutationsRollBackSingleAndPresetWrites();
        await RemoteGrantPolicyTests.PresetValidatesEveryOriginalSourceBeforeWriting();
        await RemoteGrantPolicyTests.ConcurrentRemoteWritersAndExactRevocation();
        await RemoteGrantPolicyTests.EmptyCancellationAndTrustedPendingEvidence();
        Console.WriteLine("PASS authenticated remote grant writers."); return 0;
    }
    if (args is ["--creator-grants-probe"])
    {
        await CreatorGrantPolicyTests.SixCanonicalCandidatesHaveExactScopeAndNoDelegation();
        await CreatorGrantPolicyTests.ConfirmedCreationCommitsSixWithRealActorAudit();
        await CreatorGrantPolicyTests.FailedMissingAndExistingCreationNeverGrant();
        await CreatorGrantPolicyTests.CurrentTransportAndCreateAuthorityAreRequired();
        await CreatorGrantPolicyTests.ConfirmationAndAuditMutationsRollBackEverything();
        await CreatorGrantPolicyTests.ConcurrentFinalizationAndCreateRevocationStaySeparate();
        await CreatorGrantPolicyTests.MachineGrantDoesNotAuthorizeEveryLocalUser();
        await CreatorGrantPolicyTests.PendingPinAndCancellationRespectCurrentEvidence();
        Console.WriteLine("PASS approved six-permission creator policy."); return 0;
    }
    if (args is ["--historical-reissue-probe"])
    {
        await HistoricalGrantReissueTests.PureRulesRequireOwnerAndHistoricalRoots();
        await HistoricalGrantReissueTests.NewRootsPreserveHistoryAndActualAudit();
        await HistoricalGrantReissueTests.InvalidSelectionsAndNonOwnerHaveNoEffects();
        await HistoricalGrantReissueTests.CurrentPeerStateIsRequired();
        await HistoricalGrantReissueTests.LaterAuditAndAuthorityMutationRollBack();
        await HistoricalGrantReissueTests.ConcurrentSelectionAndFreshExplicitRepeat();
        await HistoricalGrantReissueTests.EmptyAndCancelledCallsDoNotWrite();
        Console.WriteLine("PASS canonical historical grant reissue."); return 0;
    }
    if (args is ["--role-preset-probe"])
    {
        await RolePresetTests.ImmutableShapesAndPureCanonicalExpansion();
        await RolePresetTests.MixedPresetPersistsExactRowsAndAudit();
        await RolePresetTests.AnyUnauthorizedEntryRejectsWholePreset();
        await RolePresetTests.AuditAndSourceChangesRollBackWholeBatch();
        await RolePresetTests.ConcurrentPresetsOwnerRootsAndExactRevocation();
        await RolePresetTests.EmptyAndCancelledPresetHaveNoEffects();
        Console.WriteLine("PASS canonical role presets."); return 0;
    }
    if (args is ["--default-grants-probe"])
    {
        await DefaultGrantPolicyTests.FactoryShapesAndUpgrade();
        await DefaultGrantPolicyTests.OnlyFreshOwnerMayConfigure();
        await DefaultGrantPolicyTests.CurrentActivationDefaultsAndNoRetroactivity();
        await DefaultGrantPolicyTests.ConfigurationAuditAndMutationRollback();
        await DefaultGrantPolicyTests.InnerAndOuterActivationAuditRollback();
        await DefaultGrantPolicyTests.ExpiryCancellationAndMissingFinalGuard();
        await DefaultGrantPolicyTests.ConcurrentActivationUsesIndependentGuards();
        await DefaultGrantPolicyTests.TwoHostActivationIsIndependent();
        Console.WriteLine("PASS configured default grants."); return 0;
    }
    if (args is ["--grant-persistence-probe"])
    {
        await GrantPolicyPersistenceTests.CanonicalWritesAndRealAudit();
        await GrantPolicyPersistenceTests.DenialsAndFreshLocalProof();
        await GrantPolicyPersistenceTests.ExactOwnerSubtreeAndExistingRevocation();
        await GrantPolicyPersistenceTests.AuditFailureAndPostAuditMutationRollback();
        await GrantPolicyPersistenceTests.PostAuditRevokeChangesAndLateCancellation();
        await GrantPolicyPersistenceTests.ConcurrentWritersAndActorTrustRevisions();
        await GrantPolicyPersistenceTests.UpgradePreservesGrantsAndRevisionFailsClosed();
        Console.WriteLine("PASS transactional grant persistence."); return 0;
    }
    if (args is ["--authorization-foundation-probe"])
    {
        await AuthorizationPolicyTests.ModelsAndProtocolMapping();
        await AuthorizationPolicyTests.ExhaustiveDelegationRightsAndTypedScope();
        await AuthorizationPolicyTests.ForestValidityAndExactSubtrees();
        await AuthorizationPolicyTests.MalformedLineagesAndStructuralOwner();
        await AuthorizationPolicyTests.DualLocalAndRemoteCeilings();
        await AuthorizationPolicyTests.SnapshotIsolationAndClosedInputs();
        Console.WriteLine("PASS pure authorization foundation."); return 0;
    }
    if (args is ["--rotation-completion-probe"])
    {
        await RotationCompletionTests.DeletionFailureAndCompletionAuditRecover();
        await RotationCompletionTests.UpgradePreservesIntentWithoutInventingDeletion();
        await RotationCompletionTests.RecoverySupersedesIntentAndFinalAuditRollsBack();
        await RotationCompletionTests.EveryPeerMustResolveAndExpiryIsNotProof();
        await RotationCompletionTests.CurrentIncarnationAndFirstNewProof();
        await RotationCompletionTests.FinalAuditAndScopeRollback();
        await RotationCompletionTests.WriterQueueRechecksOwnerPeerAndCancellation();
        await RotationCompletionTests.InvalidMetadataAndCompletedScopeRefuse();
        await HostGenerationTransitionTests.ActualCutoverNewTlsAndReceipt();
        await HostGenerationTransitionTests.DiscoveryDrainPrecedesCutoverAndFreshGeneration();
        await HostGenerationTransitionTests.CompletionPreflightAndReconciliationRecovery();
        await HostGenerationTransitionTests.CompletionDrainRechecksOwnerAndRelationship();
        await HostGenerationTransitionTests.CompletionAuditCleanupFailureCannotCommit();
        Console.WriteLine("PASS rotation completion persistence and actual owned generation."); return 0;
    }
    if (args is ["--local-binding-provenance-probe"])
    {
        await PeerLocalBindingEvidenceTests.ConservativeUpgradeAndRollback();
        await PeerLocalBindingEvidenceTests.OwnerBindingContinuityAndInvalidation();
        await PeerLocalBindingEvidenceTests.AtomicAuditAndFinalContextRollback();
        await PeerLocalBindingEvidenceTests.QueuedOwnerChangeAndFirstNewBinding();
        Console.WriteLine("PASS local binding provenance."); return 0;
    }
    if (args is ["--current-credential-confirmation-probe"])
    {
        await ProtocolTests.SchemaEvolution();
        await PeerRelationshipIncarnationTests.CurrentConfirmationUpgradeCancellationAndFirstNewPairing();
        await PeerRotationReceiptRpcTests.CurrentConfirmationRepairsClearedLegacyEvidence();
        await PeerRotationReceiptRpcTests.CurrentConfirmationDoesNotInventHistoryOrClearReceipt();
        await PeerRotationReceiptRpcTests.CurrentConfirmationProtocolAndTrustRefusals();
        await PeerRotationReceiptRpcTests.CurrentConfirmationRejectsOldAndUnobservedPending();
        await PeerRotationReceiptRpcTests.CurrentConfirmationReplyFaultsAndStateChangeRetry();
        await PeerRotationReceiptRpcTests.CurrentConfirmationAuditAndQueuedWriterRollback();
        Console.WriteLine("PASS explicit current-credential confirmation."); return 0;
    }
    if (args is ["--peer-relationship-provenance-probe"])
    {
        await PeerRelationshipIncarnationTests.ConservativeUpgradeAndMigrationRollback();
        await PeerRelationshipIncarnationTests.ActivationRoutinePromotionAndUnrelatedPeersPreserveEvidence();
        await PeerRelationshipIncarnationTests.SameIdentityAbaAndPairingChangesInvalidate();
        await PeerRelationshipIncarnationTests.QueuedReceiptAuditRollbackAndMetadataRefusal();
        await PeerRotationReceiptRpcTests.NegotiatedRelationshipCannotSurviveIdenticalStateAba();
        Console.WriteLine("PASS relationship-specific promotion provenance."); return 0;
    }
    if (args is ["--local-owner-activation-probe"])
    {
        await ProtocolTests.SchemaEvolution();
        await LocalOwnerActivationTests.ExactOwnerAndIdempotentActivation();
        await LocalOwnerActivationTests.HookAndCancellationRollback();
        await LocalOwnerActivationTests.FinalWriterFreshness();
        await LocalOwnerPairingRpcTests.ActivationBoundaries();
        Console.WriteLine("PASS local Owner activation foundations and RPC."); return 0;
    }
    if (args is ["--local-owner-pairing-rpc-probe"])
    {
        await ProtocolTests.SchemaEvolution();
        await LocalOwnerPairingRpcTests.CapabilityAndNativeIdentity();
        await LocalOwnerPairingRpcTests.DiscoveryAndRequestBounds();
        await LocalOwnerPairingRpcTests.StaleOwnerAndResponseConstruction();
        Console.WriteLine("PASS protected local Owner pairing RPC."); return 0;
    }
    if (args is ["--local-owner-pairing-probe"])
    {
        await LocalOwnerPairingTests.RepositoryBoundaries();
        await LocalOwnerPairingTests.WriterQueueFreshness();
        await LocalOwnerPairingTests.AuthenticatedGenerationActions();
        await LocalOwnerPairingTests.UnreturnedInvitationCleanup();
        Console.WriteLine("PASS local Owner pairing foundations."); return 0;
    }
    if (args is ["--update-running-probe"])
    {
        await ApplicationUpdateServiceTests.TestARunningServerAloneDoesNotBlockApply();
        await ApplicationUpdateServiceTests.TestApplyingDoesNotStopASyntheticRunningServer();
        Console.WriteLine("PASS update eligibility with an observed synthetic server and apply preserves it."); return 0;
    }
    if (args is ["--generation-transitions-probe"])
    {
        await HostGenerationTransitionTests.AuditCleanupFailurePreventsEveryReplacement();
        await HostGenerationTransitionTests.ActualCutoverNewTlsAndReceipt();
        await HostGenerationTransitionTests.PreflightRefusalLeavesGenerationServing();
        await HostGenerationTransitionTests.PublicationFailuresRequireExplicitAuthoritativeRecovery();
        await HostGenerationTransitionTests.FinalOwnerAndAcceptanceChecksSurviveSlowShutdown();
        await HostGenerationTransitionTests.StopDuringActualStartupClosesReturnedCandidate();
        await HostGenerationTransitionTests.ConcurrentWorkAndStopWaitForFailureCleanup();
        await HostGenerationTransitionTests.UnexpectedListenerStopClosesEntireOwner();
        await HostGenerationTransitionTests.ReplacementOrReconciliationFailureNeverRestoresOld();
        Console.WriteLine("PASS Host generation transition coordination and failure recovery."); return 0;
    }
    if (args is ["--generation-construction-probe"])
    { await HostNetworkGenerationTests.RuntimeConstructionFailureCleansEarlierTimer(); Console.WriteLine("PASS runtime construction cleanup."); return 0; }
    if (args is ["--host-generation-probe"])
    {
        await HostNetworkGenerationTests.RuntimeConstructionFailureCleansEarlierTimer();
        await HostNetworkGenerationTests.ActualNetworkWorkAndClosedAdmission(); await HostNetworkGenerationTests.StopWaitsForWorkAndRejectsPrematureCutover();
        await HostNetworkGenerationTests.PartialStartupAndCancellationReleaseOwnedResources(); await HostNetworkGenerationTests.AuditCleanupFailureCannotAuthorizeCutover();
        await HostNetworkGenerationTests.StopDuringActualStartupWaitsBeforeCredentialDisposal(); await HostNetworkGenerationTests.DrainCallbackFailureStillCleansEveryOwnedResource();
        await HostNetworkGenerationTests.OwnedStopThenActualCutoverAndNewGenerationReceipts();
        Console.WriteLine("PASS Host generation resources, whole-operation shutdown, failure closure and actual rotation handoff."); return 0;
    }
    if (args is ["--host-traffic-probe"])
    {
        await HostTrafficLifetimeTests.CancellationWaitsForWholeOperationCleanup(); await HostTrafficLifetimeTests.ConcurrentWorkAndTerminalAdmission();
        await HostTrafficLifetimeTests.CancellationCallbackFailureStillWaitsForWork(); await HostTrafficLifetimeTests.ActualLocalConnectionsCloseAndNewTrafficIsRefused();
        await HostTrafficLifetimeTests.ActualPeerAndOutgoingPostReplyWorkAreOwned(); await HostTrafficLifetimeTests.ActualAbortedConnectionWaitsForItsDatabaseMutation();
        await HostTrafficLifetimeTests.PartialHandshakesAcrossAllThreeListenersAreDrained();
        Console.WriteLine("PASS Host traffic admission, complete-work drain, actual connections and partial TLS lifetime."); return 0;
    }
    if (args is ["--rotation-cutover-probe"])
    {
        await RotationCutoverTests.ActualProposalCutoverNewProofAndReceipts(); await RotationCutoverTests.PublicationTimeTrustOwnerAndProposalChangesRefuseCutover();
        await RotationCutoverTests.MarginAndCancellationAreCheckedAfterAudit(); await RotationCutoverTests.AuditRollbackAndSerializedRetry();
        await RotationCutoverTests.MaterialPublicationFailureAndCommittedRetry(); await RotationCutoverTests.ReconciliationAfterCommitAndScopeRefusal();
        await RotationCutoverTests.TransactionTriggerChangesAndInitialOwnerRefusal();
        Console.WriteLine("PASS atomic cutover, ordered publication, real New proof and failure/retry gates."); return 0;
    }
    if (args is ["--peer-transport-lifetime-probe"])
    {
        await RotationAcceptanceCollectorTests.DisposalDrainsActualTlsTrustCallbacks();
        Console.WriteLine("PASS transport disposal drains actual TLS trust callbacks."); return 0;
    }
    if (args is ["--rotation-acceptance-probe"])
    {
        await RotationAcceptanceCollectorTests.ActualAllPeerCollectionAndHistoryIsNotFreshEvidence();
        await RotationAcceptanceCollectorTests.MissingAddressUnreachableAndFreshRetry(); await RotationAcceptanceCollectorTests.MarginLapseAndReceiptRemainBlocked();
        await RotationAcceptanceCollectorTests.PendingAndRecoveryAreNotSilentlyExcluded(); await RotationAcceptanceCollectorTests.LateMembershipAbaAndAbortInvalidateTheRound();
        await RotationAcceptanceCollectorTests.ActualPeerKeyPromotionRequiresANewCollection(); await RotationAcceptanceCollectorTests.ElapsedBoundsScopeAndCancellation();
        await RotationAcceptanceCollectorTests.DisposalDrainsActualTlsTrustCallbacks();
        Console.WriteLine("PASS fresh multi-peer acceptance, remaining margin, dynamic trust, proof and retry gates."); return 0;
    }
    if (args is ["--rotation-peer-set-probe"])
    {
        await RotationPeerSetTests.UpgradePreservesRowsAndDoesNotInventEvidence(); await RotationPeerSetTests.TransactionRollbackAndIdenticalStateAba();
        await RotationPeerSetTests.PairingWritesAndConcurrentMembershipAreTracked(); await RotationPeerSetTests.MissingAndExhaustedRevisionFailClosed();
        await RotationPeerSetTests.PendingRecoveryAndMalformedSnapshotGates(); await RotationPeerSetTests.RealObserverAuditRollbackAndHostScope(); await RotationStagingTests.UpgradeDoesNotInventOrdering();
        Console.WriteLine("PASS dynamic peer-set revision, rollback, ABA, additive upgrade and snapshot gates."); return 0;
    }
    if (args is ["--rotation-receipt-rpc-probe"])
    {
        await ProtocolTests.SchemaEvolution(); await PeerRotationReceiptRpcTests.ActualObservationAndConcurrentReceipt();
        await PeerRotationReceiptRpcTests.LostReceiptAndReceiverAuditRetryAfterReopen(); await PeerRotationReceiptRpcTests.SenderAuditFailureKeepsPromotionRetryable();
        await PeerRotationReceiptRpcTests.ForgedReplyAndStaleReceiverStateCannotClear(); await PeerRotationReceiptRpcTests.CapabilityIdentityCurrentAndTrustGates();
        await PeerRotationReceiptRpcTests.LegacyReceiptDoesNotInventStagingHistory(); await PeerRotationReceiptRpcTests.OldPresentationIncompleteProofAndChangedReceipt();
        Console.WriteLine("PASS actual promotion receipts, independent durable commits, exact proof and retry gates."); return 0;
    }
    if (args is ["--rotation-proposal-rpc-probe"])
    {
        await ProtocolTests.SchemaEvolution(); await PeerRotationProposalRpcTests.ActualConcurrentProposalAndClockOffset();
        await PeerRotationProposalRpcTests.LostReplyAndAuditFailureResumeDurably(); await PeerRotationProposalRpcTests.LapsedAndReceiptStateCannotBeOverwritten();
        await PeerRotationProposalRpcTests.ProposalProtocolIdentityAndFreshState(); await PeerRotationProposalRpcTests.ForgedAcknowledgementAndConcurrentAbort();
        await PeerRotationProposalRpcTests.ConservativeRemainingTimeIsNotDurableAuthority(); await PeerRotationStatusRpcTests.ActualReplyForgeryAndStaleOwnerAreRefused();
        Console.WriteLine("PASS actual rotation proposals, durable acknowledgement history, replay/fault gates and conservative time bounds."); return 0;
    }
    if (args is ["--rotation-status-rpc-probe"])
    {
        await ProtocolTests.SchemaEvolution(); await PeerRotationStatusRpcTests.ActualRenewalAbortAndFreshRetry();
        await PeerRotationStatusRpcTests.ActualNewProofPromotesWithoutStatusClaim(); await PeerRotationStatusRpcTests.NegotiationActiveAndFreshTrustGates();
        await PeerRotationStatusRpcTests.WireClosedEnumsAndLocalCredentialChange(); await PeerRotationStatusRpcTests.ActualReplyForgeryAndStaleOwnerAreRefused();
        Console.WriteLine("PASS actual pinned rotation status RPC, Owner renewal, live New proof and protocol/trust gates."); return 0;
    }
    if (args is ["--rotation-reconfirmation-probe"])
    {
        await RotationReconfirmationTests.RenewalRequiresLiveIntentAndFreshOwner(); await RotationReconfirmationTests.AbandonmentAndUntrustedCutoverClaims();
        await RotationReconfirmationTests.QueryScopeReplayDeadlineAndChangedTuple(); await RotationReconfirmationTests.ConcurrentRenewalAndAuditRollback();
        await RotationReconfirmationTests.SenderStatusBindsTupleAndPresentedCurrent(); await RotationReconfirmationTests.DeadlineCrossingDuringAuditRollsBack();
        Console.WriteLine("PASS retained rotation live status, Owner renewal, stale query refusal and abandonment."); return 0;
    }
    if (args is ["--rotation-staging-probe"])
    {
        await RotationStagingTests.SenderSequenceAndOwnerGate(); await RotationStagingTests.ReceiverOrderingReplayAndRollback();
        await RotationStagingTests.LapseAndReceiptCannotBeOverwritten(); await RotationStagingTests.ClosedStatesAndIdentity(); await RotationStagingTests.UpgradeDoesNotInventOrdering();
        Console.WriteLine("PASS durable ordered proposals, receiver staging/replay gates and additive upgrade."); return 0;
    }
    if (args is ["--peer-rotation-completion-probe"])
    {
        await PeerRotationCompletionTests.PromotionReceiptAndConcurrentReplay(); await PeerRotationCompletionTests.LapseAndTransactionalRollback();
        await PeerRotationCompletionTests.InvalidAndRecoveryStates(); await PeerRotationCompletionTests.ActualTlsPresentationPromotes();
        await PeerSecurityRpcTests.ProvenRotationObservation();
        await PeerSecurityRpcTests.RecordedPendingPin();
        Console.WriteLine("PASS durable peer promotion, retained receipt, lapse/rollback and actual TLS observation."); return 0;
    }
    if (args is ["--rotation-material-probe"])
    {
        await RoutineRotationMaterialTests.DurableWriteRetryAndSerialization(); await RoutineRotationMaterialTests.CancellationAuthorizationAndAuditRollback(); await RoutineRotationMaterialTests.InvalidExistingMaterialNeverReplaced();
        Console.WriteLine("PASS recoverable rotation material, current Owner/audit commit gates and invalid-material refusal."); return 0;
    }
    if (args is ["--rotation-preparation-probe"])
    {
        await RoutineRotationPreparationTests.SerializedAndResumable(); await RoutineRotationPreparationTests.OwnerFreshnessAndRollback(); await RoutineRotationPreparationTests.AbortRetentionAndCutoverGate();
        Console.WriteLine("PASS serialized routine rotation preparation, current Owner checks and safe abort/retention."); return 0;
    }
    if (args is ["--pairing-audit-probe"])
    {
        await PairingAuditTests.IdempotenceAndPrivacy(); await PairingAuditTests.RetryAndPendingCleanup(); await PairingAuditTests.CapacityAndShutdownFailure(); await PeerPairingRpcTests.AuditFailureBlocksAdmission();
        Console.WriteLine("PASS durable pairing terminal audit, storage retry and pending cleanup."); return 0;
    }
    if (args is ["--windows-discovery-configuration-probe"])
    {
        await WindowsDiscoveryConfigurationTests.ExplicitPortAndCompatibleBindings();
        await WindowsDiscoveryConfigurationTests.ActualPortCollisionAndPreCancellation();
        Console.WriteLine("Windows discovery configuration probe passed."); return 0;
    }
    if (args is ["--generation-discovery-probe"])
    {
        await HostGenerationDiscoveryTests.ActualBoundMetadataAndSealedConfiguration();
        await HostGenerationDiscoveryTests.StopStartsDiscoveryBeforeTrafficDrain();
        await HostGenerationDiscoveryTests.StartupCancellationOwnsReturnedDiscovery();
        await HostGenerationDiscoveryTests.StartupFailureAndUnavailableManualService();
        await HostGenerationDiscoveryTests.FatalCompletionAndCleanupRefuseCutover();
        await HostGenerationTransitionTests.DiscoveryDrainPrecedesCutoverAndFreshGeneration();
        Console.WriteLine("Generation discovery probe passed."); return 0;
    }
    if (args is ["--host-discovery-runtime-probe"])
    {
        await HostDiscoveryRuntimeTests.StartupMetadataAndExpiry();
        await HostDiscoveryRuntimeTests.BoundedRetriesAndFreshReceiver();
        await HostDiscoveryRuntimeTests.StopDrainsEveryOwnedActivity();
        await HostDiscoveryRuntimeTests.FatalFailuresNeverRetryOrHideCleanup();
        await HostDiscoveryRuntimeTests.SenderFailureDrainsReceiverAndCancellation();
        await HostDiscoveryRuntimeTests.ActualLoopbackPacketsAndRelease();
        Console.WriteLine("Host discovery runtime probe passed."); return 0;
    }
    if (args is ["--windows-lan-availability-probe"])
    {
        await WindowsLanAvailabilityTests.ExactOriginsAndSocketSetupCleanup(); await WindowsLanAvailabilityTests.ReceiverKeepsCallbackAndCleanupFailuresFatal();
        await WindowsLanAvailabilityTests.RoundRequiresCleanOwnedTemporaryFailure();
        Console.WriteLine("Windows LAN availability probe passed."); return 0;
    }
    if (args is ["--windows-lan-broadcaster-probe"])
    {
        await WindowsLanBroadcasterTests.BoundedFreshRoundAndPublicPacket(); await WindowsLanBroadcasterTests.FailureCancellationAndOwnedCleanup();
        await WindowsLanBroadcasterTests.ActualWindowsSocketConstraints();
        Console.WriteLine("Windows LAN broadcaster probe passed."); return 0;
    }
    if (args is ["--windows-lan-receiver-probe"])
    {
        await WindowsLanReceiverTests.ActualPacketsAndFreshAdmission(); await WindowsLanReceiverTests.StopDrainsSerializedCallback();
        await WindowsLanReceiverTests.WorkerAndCancellationFailures(); await WindowsLanReceiverTests.ExplicitPortAndExclusiveOwnership();
        Console.WriteLine("Windows LAN receiver probe passed."); return 0;
    }
    if (args is ["--windows-lan-interfaces-probe"])
    {
        await WindowsLanInterfaceTests.LinkSourcePolicy(); await WindowsLanInterfaceTests.HardwareEligibilityAndCorrelation();
        await WindowsLanInterfaceTests.NativeLayoutAndReadOnlyInventory();
        Console.WriteLine("PASS Windows LAN source policy, hardware eligibility and read-only native inventory."); return 0;
    }
    if (args is ["--host-discovery-probe"])
    {
        await HostDiscoveryTests.ManualAddresses(); await HostDiscoveryTests.PacketBoundsAndCompatibility();
        await HostDiscoveryTests.DirectorySourceBoundsAndExpiry(); await ProtocolTests.SchemaEvolution();
        Console.WriteLine("PASS manual Host addresses and unverified bounded discovery metadata/directory."); return 0;
    }
    if (args is ["--peer-pairing-probe"])
    {
        await ProtocolTests.SchemaEvolution(); await PeerPairingRpcTests.AdmissionAndFrameOrder(); await PeerPairingRpcTests.DisconnectAndSingleConnection();
        Console.WriteLine("PASS first-contact protocol admission and connection cleanup (synthetic lifecycle provider only)."); return 0;
    }
    if (args is ["--peer-pairing-native-probe", var nativePairingPath])
    { await PeerPairingRpcTests.Native(nativePairingPath); return 0; }
    if (args is ["--peer-grpc-probe"])
    {
        await ProtocolTests.SchemaEvolution();
        await PeerSecurityRpcTests.ActualActivationAndLostReply(); await PeerSecurityRpcTests.ProtocolAndConnectionIdentity();
        await PeerSecurityRpcTests.TlsRefusalsAndLimits(); await PeerSecurityRpcTests.RecordedPendingPin();
        await PeerSecurityRpcTests.ReconnectRequiresFreshTransport();
        Console.WriteLine("PASS actual pinned Host gRPC activation and refusal probes."); return 0;
    }
    if (args is ["--peer-activation-probe"])
    {
        await PeerActivationTests.ReciprocalReopenAndLostReply(); await PeerActivationTests.ConcurrentAndRollback();
        await PeerActivationTests.IdentityExpiryAndRecovery(); await PeerActivationTests.DeadlineAndMissingProof();
        Console.WriteLine("PASS durable reciprocal activation probes."); return 0;
    }
    if (args is ["--native-own-recovery-probe", var ownRecoveryPath])
    {
        using var provider = new PalworldServerManager.Platform.Windows.WindowsSpake2Provider(ownRecoveryPath,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ownRecoveryPath))));
        await LocalOwnerPairingRpcTests.NativeSimultaneousOwnRecovery(provider); return 0;
    }
    if (args is ["--native-repair-probe", var repairPath])
    {
        using var provider = new PalworldServerManager.Platform.Windows.WindowsSpake2Provider(repairPath,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(repairPath))));
        await LocalOwnerPairingRpcTests.NativeRevocationAndRepair(provider); return 0;
    }
    if (args is ["--spake2-wrapper-probe", var nativePath])
    {
        Spake2WrapperTests.Run(nativePath); PairingAttemptTests.Native(nativePath); await PeerPairingRpcTests.Native(nativePath); return 0;
    }
    if (args is ["--spake2-wrapper-probe", var nativeProduction, var nativeFault])
    {
        Spake2WrapperTests.Run(nativeProduction, nativeFault); PairingAttemptTests.Native(nativeProduction); await PeerPairingRpcTests.Native(nativeProduction); return 0;
    }
    if (args is ["--pairing-lifecycle-probe"])
    {
        await PairingAttemptTests.Lifecycle(); await PairingAttemptTests.ExpiryAndCleanup(); await PairingAttemptTests.BoundsAndCancellation(); await PairingAttemptTests.AdvertisedSelection(); await PairingAttemptTests.AdvertisedAdmissionIsAtomic();
        Console.WriteLine("PASS Host pairing lifecycle probes."); return 0;
    }
    if (args is ["--peer-trust-probe"])
    {
        await PeerTrustTests.DurableAndIdempotent(); await PeerTrustTests.ExpiryAndExistingIdentity();
        await PeerTrustTests.RollbackAndCredentialRaces(); await PeerTrustTests.PriorSchemaDoesNotInventProof();
        await PeerTrustTests.OfflineRecoveryBlocksPendingTrust();
        Console.WriteLine("PASS durable PeerBound storage probes."); return 0;
    }
    if (args is ["--peer-tls-probe"])
    {
        await PeerTlsTests.MutualProof(); await PeerTlsTests.RefusalsAndCleanup(); await PeerTlsTests.TrustPurposes();
        Console.WriteLine("PASS actual mutual peer TLS and trust-purpose probes."); return 0;
    }
    if (args is ["--client-security-probe"])
    {
        await ClientSecurityCompositionTests.BootstrapLostReply();
        await ClientSecurityCompositionTests.RotationBindingAndDeletion();
        await ClientSecurityCompositionTests.RehomeKeyChoice();
        await ClientSecurityCompositionTests.EnrollmentAndAuthority();
        await ClientSecurityCompositionTests.ActivationAndErrors();
        await ClientSecurityCompositionTests.NegotiationAndProofBoundaries();
        Console.WriteLine("PASS client security composition probe"); return 0;
    }
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
    ("Failed generation audit cleanup prevents cutover and every replacement", HostGenerationTransitionTests.AuditCleanupFailurePreventsEveryReplacement),
    ("Host transitions perform real cutover New TLS and peer receipt", HostGenerationTransitionTests.ActualCutoverNewTlsAndReceipt),
    ("Host transition preflight refuses without disrupting service", HostGenerationTransitionTests.PreflightRefusalLeavesGenerationServing),
    ("Publication failures leave transitions quiesced until explicit recovery", HostGenerationTransitionTests.PublicationFailuresRequireExplicitAuthoritativeRecovery),
    ("Slow shutdown cannot bypass fresh Owner or acceptance margin", HostGenerationTransitionTests.FinalOwnerAndAcceptanceChecksSurviveSlowShutdown),
    ("Stop during actual generation startup closes the returned candidate", HostGenerationTransitionTests.StopDuringActualStartupClosesReturnedCandidate),
    ("Host transition stop cancels queued work and waits failed callback cleanup", HostGenerationTransitionTests.ConcurrentWorkAndStopWaitForFailureCleanup),
    ("Unexpected listener stopping closes the complete transition owner", HostGenerationTransitionTests.UnexpectedListenerStopClosesEntireOwner),
    ("Replacement or reconciliation failure cannot restore an old credential", HostGenerationTransitionTests.ReplacementOrReconciliationFailureNeverRestoresOld),

    ("Failed pairing runtime construction cleans its previously created audit timer", HostNetworkGenerationTests.RuntimeConstructionFailureCleansEarlierTimer),
    ("Host generation owns actual network work and closes every outgoing helper", HostNetworkGenerationTests.ActualNetworkWorkAndClosedAdmission),
    ("Host generation stop waits for work before releasing the credential", HostNetworkGenerationTests.StopWaitsForWorkAndRejectsPrematureCutover),
    ("Host generation startup refusal and cancellation clean partial resources", HostNetworkGenerationTests.PartialStartupAndCancellationReleaseOwnedResources),
    ("Host generation audit cleanup failure never supplies cutover readiness", HostNetworkGenerationTests.AuditCleanupFailureCannotAuthorizeCutover),
    ("Host generation stop during actual startup waits for resource ownership", HostNetworkGenerationTests.StopDuringActualStartupWaitsBeforeCredentialDisposal),
    ("Host generation drain callback failure still cleans every owned resource", HostNetworkGenerationTests.DrainCallbackFailureStillCleansEveryOwnedResource),
    ("Owned generation shutdown precedes actual cutover New TLS and receipt", HostNetworkGenerationTests.OwnedStopThenActualCutoverAndNewGenerationReceipts),

    ("Host traffic draining waits for canceled whole-operation cleanup", HostTrafficLifetimeTests.CancellationWaitsForWholeOperationCleanup),
    ("Host traffic lifetime closes concurrent admission permanently", HostTrafficLifetimeTests.ConcurrentWorkAndTerminalAdmission),
    ("Host traffic callback failure cannot bypass active work draining", HostTrafficLifetimeTests.CancellationCallbackFailureStillWaitsForWork),
    ("Actual local Host connections close and new traffic is refused", HostTrafficLifetimeTests.ActualLocalConnectionsCloseAndNewTrafficIsRefused),
    ("Actual peer and post-reply outgoing work remain lifetime-owned", HostTrafficLifetimeTests.ActualPeerAndOutgoingPostReplyWorkAreOwned),
    ("Aborted actual peer connections wait for their in-flight Host mutation", HostTrafficLifetimeTests.ActualAbortedConnectionWaitsForItsDatabaseMutation),
    ("Partial TLS across local peer and pairing listeners is drained", HostTrafficLifetimeTests.PartialHandshakesAcrossAllThreeListenersAreDrained),

    ("Cutover refuses initial Owner mismatch and rolls back audit-triggered trust changes", RotationCutoverTests.TransactionTriggerChangesAndInitialOwnerRefusal),
    ("Actual multi-peer proposal acceptance cuts over before New proof and receipts", RotationCutoverTests.ActualProposalCutoverNewProofAndReceipts),
    ("Cutover rechecks peer trust Owner and proposal after staged publication", RotationCutoverTests.PublicationTimeTrustOwnerAndProposalChangesRefuseCutover),
    ("Cutover rechecks elapsed margin and cancellation after audit before commit", RotationCutoverTests.MarginAndCancellationAreCheckedAfterAudit),
    ("Cutover audit failure rolls back and serialized retries record one transition", RotationCutoverTests.AuditRollbackAndSerializedRetry),
    ("Cutover material and publication failures preserve durable truth across retry", RotationCutoverTests.MaterialPublicationFailureAndCommittedRetry),
    ("Cutover restart reconciliation retains both keys and wrong collection scope is refused", RotationCutoverTests.ReconciliationAfterCommitAndScopeRefusal),
    ("Transport disposal drains in-flight actual TLS trust callbacks", RotationAcceptanceCollectorTests.DisposalDrainsActualTlsTrustCallbacks),
    ("Actual multi-peer acceptance covers every peer and history cannot replace a fresh round", RotationAcceptanceCollectorTests.ActualAllPeerCollectionAndHistoryIsNotFreshEvidence),
    ("Missing and unreachable peers block acceptance until a fresh actual retry", RotationAcceptanceCollectorTests.MissingAddressUnreachableAndFreshRetry),
    ("Acceptance collection enforces remaining margin lapse and receipt gates", RotationAcceptanceCollectorTests.MarginLapseAndReceiptRemainBlocked),
    ("Unresolved pending and recovery peers remain acceptance blockers", RotationAcceptanceCollectorTests.PendingAndRecoveryAreNotSilentlyExcluded),
    ("Late membership restored trust and concurrent abort invalidate acceptance collection", RotationAcceptanceCollectorTests.LateMembershipAbaAndAbortInvalidateTheRound),
    ("Actual peer key promotion requires a new collection with the current peer set", RotationAcceptanceCollectorTests.ActualPeerKeyPromotionRequiresANewCollection),
    ("Acceptance bounds expire monotonically and collections reject wrong scope or cancellation", RotationAcceptanceCollectorTests.ElapsedBoundsScopeAndCancellation),
    ("Peer observer audit rollback preserves revision and rotation snapshots require exact Host scope", RotationPeerSetTests.RealObserverAuditRollbackAndHostScope),
    ("Peer revision upgrade preserves existing trust and does not invent acknowledgement evidence", RotationPeerSetTests.UpgradePreservesRowsAndDoesNotInventEvidence),
    ("Peer revision rolls back with trust and detects identical-state ABA changes", RotationPeerSetTests.TransactionRollbackAndIdenticalStateAba),
    ("Pairing metadata and concurrent peer membership updates advance the same revision", RotationPeerSetTests.PairingWritesAndConcurrentMembershipAreTracked),
    ("Missing exhausted and malformed peer revision fail closed without partial trust writes", RotationPeerSetTests.MissingAndExhaustedRevisionFailClosed),
    ("Rotation peer snapshots preserve unresolved pending recovery and malformed-state gates", RotationPeerSetTests.PendingRecoveryAndMalformedSnapshotGates),
    ("Old TLS and incomplete proof cannot report promotion or clear a changed receipt", PeerRotationReceiptRpcTests.OldPresentationIncompleteProofAndChangedReceipt),
    ("Actual New observation and concurrent receipt commit exactly once without changing grants", PeerRotationReceiptRpcTests.ActualObservationAndConcurrentReceipt),
    ("Lost promotion reply and receiver audit failure preserve durable retry across reopen", PeerRotationReceiptRpcTests.LostReceiptAndReceiverAuditRetryAfterReopen),
    ("Rotating Host audit failure keeps peer promotion receipt retryable", PeerRotationReceiptRpcTests.SenderAuditFailureKeepsPromotionRetryable),
    ("Forged promotion reply and changed current receiver state cannot erase receipt", PeerRotationReceiptRpcTests.ForgedReplyAndStaleReceiverStateCannotClear),
    ("Receipt RPC requires negotiated capability exact current rotation and Active trust", PeerRotationReceiptRpcTests.CapabilityIdentityCurrentAndTrustGates),
    ("Legacy completed rotation receipt does not invent absent staging acknowledgement history", PeerRotationReceiptRpcTests.LegacyReceiptDoesNotInventStagingHistory),
    ("Actual concurrent rotation proposals preserve deadlines across unrelated Host clocks", PeerRotationProposalRpcTests.ActualConcurrentProposalAndClockOffset),
    ("Lost rotation replies and sender audit failure resume from receiver durable staging", PeerRotationProposalRpcTests.LostReplyAndAuditFailureResumeDurably),
    ("Actual proposals cannot overwrite lapsed staging or pending promotion receipts", PeerRotationProposalRpcTests.LapsedAndReceiptStateCannotBeOverwritten),
    ("Proposal RPC enforces negotiation exact identity and both current TLS credentials", PeerRotationProposalRpcTests.ProposalProtocolIdentityAndFreshState),
    ("Forged proposal acknowledgements and concurrent abort cannot create sender history", PeerRotationProposalRpcTests.ForgedAcknowledgementAndConcurrentAbort),
    ("Proposal acceptance bounds subtract elapsed time without assuming UTC agreement", PeerRotationProposalRpcTests.ConservativeRemainingTimeIsNotDurableAuthority),
    ("Actual rotation status replies cannot replay correlation forge cutover or reuse stale Owner authority", PeerRotationStatusRpcTests.ActualReplyForgeryAndStaleOwnerAreRefused),
    ("Actual pinned rotation status renews with Owner intent and recovers abort after failed contact", PeerRotationStatusRpcTests.ActualRenewalAbortAndFreshRetry),
    ("Rotation status connection promotes only with actual New possession proof", PeerRotationStatusRpcTests.ActualNewProofPromotesWithoutStatusClaim),
    ("Rotation status RPC requires negotiation Active trust and fresh identity checks", PeerRotationStatusRpcTests.NegotiationActiveAndFreshTrustGates),
    ("Rotation status denies unknown wire states and changed local connection credentials", PeerRotationStatusRpcTests.WireClosedEnumsAndLocalCredentialChange),
    ("Rotation query expiry before commit rolls back renewal and its audit", RotationReconfirmationTests.DeadlineCrossingDuringAuditRollsBack),
    ("Retained rotation renewal requires fresh Owner intent and live exact status", RotationReconfirmationTests.RenewalRequiresLiveIntentAndFreshOwner),
    ("Old-key status claims never promote and live abandonment preserves grants", RotationReconfirmationTests.AbandonmentAndUntrustedCutoverClaims),
    ("Rotation status query scope monotonic expiry and changed tuples deny replay", RotationReconfirmationTests.QueryScopeReplayDeadlineAndChangedTuple),
    ("Rotation renewal serializes concurrent replies and rolls back failed audits", RotationReconfirmationTests.ConcurrentRenewalAndAuditRollback),
    ("Sender rotation status binds immutable proposal and actually presented current key", RotationReconfirmationTests.SenderStatusBindsTupleAndPresentedCurrent),
    ("Routine rotation proposals retain a monotonic identity with current Owner and audit gates", RotationStagingTests.SenderSequenceAndOwnerGate),
    ("Peer rotation staging serializes replay and rejects stale or changed proposals", RotationStagingTests.ReceiverOrderingReplayAndRollback),
    ("Peer rotation proposals cannot overwrite lapsed staging or an unconfirmed promotion receipt", RotationStagingTests.LapseAndReceiptCannotBeOverwritten),
    ("Peer rotation staging refuses invalid sequences identities and inactive or recovery trust", RotationStagingTests.ClosedStatesAndIdentity),
    ("Rotation proposal metadata upgrade preserves existing trust without inventing ordering", RotationStagingTests.UpgradeDoesNotInventOrdering),
    ("Peer rotation promotion and durable receipt retries serialize without changing grants", PeerRotationCompletionTests.PromotionReceiptAndConcurrentReplay),
    ("Peer rotation lapse retains live pins and every transition rolls back on failed audit", PeerRotationCompletionTests.LapseAndTransactionalRollback),
    ("Peer rotation refuses incomplete metadata and inactive or recovery-required trust", PeerRotationCompletionTests.InvalidAndRecoveryStates),
    ("Actual completed mutual TLS with the staged key promotes trust and rejects the old key", PeerRotationCompletionTests.ActualTlsPresentationPromotes),
    ("Outbound rotation observation waits for proven TLS and runs in the real pinned RPC client", PeerSecurityRpcTests.ProvenRotationObservation),
    ("Routine rotation material recovers durable writes and serializes retries without changing identity", RoutineRotationMaterialTests.DurableWriteRetryAndSerialization),
    ("Routine rotation material preserves reservations on cancellation and rejects stale Owner or audit failure", RoutineRotationMaterialTests.CancellationAuthorizationAndAuditRollback),
    ("Routine rotation never replaces corrupt, missing recorded or unusable existing key material", RoutineRotationMaterialTests.InvalidExistingMaterialNeverReplaced),
    ("Routine rotation preparation is serialized and preserves a reserved credential across reopen", RoutineRotationPreparationTests.SerializedAndResumable),
    ("Routine rotation rechecks Owner identity and rolls back every transition on audit failure", RoutineRotationPreparationTests.OwnerFreshnessAndRollback),
    ("Routine rotation abort preserves current trust and cannot bypass CutOver retention", RoutineRotationPreparationTests.AbortRetentionAndCutoverGate),
    ("Pairing terminal audits are idempotent public records with no claimed actor", PairingAuditTests.IdempotenceAndPrivacy),
    ("Pairing audit retries preserve outcome time and pending expiry commits atomically", PairingAuditTests.RetryAndPendingCleanup),
    ("Pairing audit overflow stays failed closed and shutdown reports unavailable storage", PairingAuditTests.CapacityAndShutdownFailure),
    ("Actual pairing disconnect cleans native state despite audit failure and closes admission", PeerPairingRpcTests.AuditFailureBlocksAdmission),
    ("First-contact pairing rejects invalid protocol frames and exposes no activation RPC", PeerPairingRpcTests.AdmissionAndFrameOrder),
    ("First-contact pairing cleans disconnected exchanges and permits one attempt per TLS connection", PeerPairingRpcTests.DisconnectAndSingleConnection),
    ("Host peer gRPC resumes durable activation after listener restart and discarded reply", PeerSecurityRpcTests.ActualActivationAndLostReply),
    ("Host peer gRPC binds negotiation and live trust to the actual TLS connection", PeerSecurityRpcTests.ProtocolAndConnectionIdentity),
    ("Host peer gRPC rejects wrong TLS pins expired trust oversized messages and cancellation", PeerSecurityRpcTests.TlsRefusalsAndLimits),
    ("Host peer gRPC recognizes authenticated pending rotation pins without recreating grants", PeerSecurityRpcTests.RecordedPendingPin),
    ("Host peer gRPC cannot reuse transport identity across a second TLS connection", PeerSecurityRpcTests.ReconnectRequiresFreshTransport),
    ("Peer activation requires reciprocal durable identity and retries after a lost reply", PeerActivationTests.ReciprocalReopenAndLostReply),
    ("Peer activation hook and audit commit once with concurrent retries and rollback", PeerActivationTests.ConcurrentAndRollback),
    ("Peer activation rejects identity expiry recovery and credential replacement shortcuts", PeerActivationTests.IdentityExpiryAndRecovery),
    ("Peer activation cannot invent old binding proof or extend its deadline during a slow hook", PeerActivationTests.DeadlineAndMissingProof),
    ("Peer TLS proves mutual private-key possession over pinned HTTP/2 connections", PeerTlsTests.MutualProof),
    ("Peer TLS rejects wrong pins missing private proof protocol mismatch and cancellation", PeerTlsTests.RefusalsAndCleanup),
    ("Peer TLS identity is rechecked against trust state and never grants authority", PeerTlsTests.TrustPurposes),
    ("PeerBound persists with zero grants and idempotent concurrent retries", PeerTrustTests.DurableAndIdempotent),
    ("Expired PeerBound retains a tombstone and blocks implicit credential replacement", PeerTrustTests.ExpiryAndExistingIdentity),
    ("Peer binding and expiry roll back atomically with audit and credential races", PeerTrustTests.RollbackAndCredentialRaces),
    ("Prior schema PeerBound metadata never fabricates local identity proof", PeerTrustTests.PriorSchemaDoesNotInventProof),
    ("Offline Host credential recovery blocks pending peers and stale replacement candidates", PeerTrustTests.OfflineRecoveryBlocksPendingTrust),
    ("Host pairing codes enforce global failure limits and trusted-source backoff", PairingAttemptTests.Lifecycle),
    ("Host pairing expiry is monotonic and disconnect/restart clear transient exchanges", PairingAttemptTests.ExpiryAndCleanup),
    ("Host pairing residency, cancellation and broken audit sinks preserve cleanup", PairingAttemptTests.BoundsAndCancellation),
    ("Windows discovery classifies exact IO errors only after socket setup cleanup", WindowsLanAvailabilityTests.ExactOriginsAndSocketSetupCleanup),
    ("Windows discovery never downgrades callback or cancellation cleanup failures", WindowsLanAvailabilityTests.ReceiverKeepsCallbackAndCleanupFailuresFatal),
    ("Windows discovery temporary send failure requires clean owned disposal", WindowsLanAvailabilityTests.RoundRequiresCleanOwnedTemporaryFailure),
    ("Windows discovery rounds copy public data and refresh exact bounded LAN links", WindowsLanBroadcasterTests.BoundedFreshRoundAndPublicPacket),
    ("Windows discovery sends preserve failure cancellation and owned cleanup", WindowsLanBroadcasterTests.FailureCancellationAndOwnedCleanup),
    ("Windows discovery sender constrains the real interface and loopback datagram", WindowsLanBroadcasterTests.ActualWindowsSocketConstraints),
    ("Windows discovery uses actual packet metadata and fresh bounded admission", WindowsLanReceiverTests.ActualPacketsAndFreshAdmission),
    ("Windows discovery shutdown drains its serialized callback", WindowsLanReceiverTests.StopDrainsSerializedCallback),
    ("Windows discovery preserves worker and cancellation failures after cleanup", WindowsLanReceiverTests.WorkerAndCancellationFailures),
    ("Windows discovery requires an explicit exclusive production port", WindowsLanReceiverTests.ExplicitPortAndExclusiveOwnership),
    ("LAN discovery source policy requires the exact interface and subnet host", WindowsLanInterfaceTests.LinkSourcePolicy),
    ("Windows LAN eligibility rejects virtual tunnels and changed adapter identity", WindowsLanInterfaceTests.HardwareEligibilityAndCorrelation),
    ("Windows LAN native layout and read-only adapter inventory match the SDK", WindowsLanInterfaceTests.NativeLayoutAndReadOnlyInventory),
    ("Windows discovery requires explicit port and compatible listener bindings", WindowsDiscoveryConfigurationTests.ExplicitPortAndCompatibleBindings),
    ("Windows concrete discovery refuses port collision and pre-cancellation", WindowsDiscoveryConfigurationTests.ActualPortCollisionAndPreCancellation),
    ("Discovery drain precedes actual cutover and fresh generation", HostGenerationTransitionTests.DiscoveryDrainPrecedesCutoverAndFreshGeneration),
    ("Generation CompletionPreflightAndReconciliationRecovery", HostGenerationTransitionTests.CompletionPreflightAndReconciliationRecovery),
    ("Generation CompletionDrainRechecksOwnerAndRelationship", HostGenerationTransitionTests.CompletionDrainRechecksOwnerAndRelationship),
    ("Generation CompletionAuditCleanupFailureCannotCommit", HostGenerationTransitionTests.CompletionAuditCleanupFailureCannotCommit),
    ("Successful permission audit RemovedAuditRollsBackGrant", PermissionSuccessAuditTests.RemovedAuditRollsBackGrant),
    ("Successful permission audit EverySuccessfulPathChecksEveryAuditField", PermissionSuccessAuditTests.EverySuccessfulPathChecksEveryAuditField),
    ("Successful permission audit LaterBatchAndEnclosingAuditCannotEraseEarlierRows", PermissionSuccessAuditTests.LaterBatchAndEnclosingAuditCannotEraseEarlierRows),
    ("Successful permission audit ConcurrentSuccessfulBatchesKeepIndependentAuditGuards", PermissionSuccessAuditTests.ConcurrentSuccessfulBatchesKeepIndependentAuditGuards),
    ("Permission dispatch LocalSignaturesFeedCanonicalOwnerAndDelegationActions", AuthenticatedPermissionDispatchTests.LocalSignaturesFeedCanonicalOwnerAndDelegationActions),
    ("Permission dispatch PeerActionsUseRealPeerAndCreatorKeepsIndependentUserCeiling", AuthenticatedPermissionDispatchTests.PeerActionsUseRealPeerAndCreatorKeepsIndependentUserCeiling),
    ("Permission dispatch LocalChannelNegotiationAndCurrentIdentityGateCallbacks", AuthenticatedPermissionDispatchTests.LocalChannelNegotiationAndCurrentIdentityGateCallbacks),
    ("Permission dispatch PeerChannelNegotiationAndOriginalProofGateCallbacks", AuthenticatedPermissionDispatchTests.PeerChannelNegotiationAndOriginalProofGateCallbacks),
    ("Permission dispatch CallLifetimeCancellationAndRuntimeAssociationAreBound", AuthenticatedPermissionDispatchTests.CallLifetimeCancellationAndRuntimeAssociationAreBound),
    ("Permission dispatch GuardedPromotionDoesNotBecomePermissionOrLocalUserAuthority", AuthenticatedPermissionDispatchTests.GuardedPromotionDoesNotBecomePermissionOrLocalUserAuthority),
    ("Bound peer observation CurrentLapsedAndPendingPreserveIdentityAndExactEffects", BoundPeerObservationTests.CurrentLapsedAndPendingPreserveIdentityAndExactEffects),
    ("Bound peer observation WrongConnectionAndInactiveTrustNeverObserve", BoundPeerObservationTests.WrongConnectionAndInactiveTrustNeverObserve),
    ("Bound peer observation AuditHistoryAndLateProofMutationsRollBack", BoundPeerObservationTests.AuditHistoryAndLateProofMutationsRollBack),
    ("Bound peer observation CancellationConcurrencyAndMissingRevisionFailClosed", BoundPeerObservationTests.CancellationConcurrencyAndMissingRevisionFailClosed),
    ("Capability use EveryTypedCapabilityUsesOnlyItsExactGrant", CapabilityUseTests.EveryTypedCapabilityUsesOnlyItsExactGrant),
    ("Capability use QualifiedTargetsAndRevocationInvalidateEarlierObservations", CapabilityUseTests.QualifiedTargetsAndRevocationInvalidateEarlierObservations),
    ("Capability use TwoHostCeilingsAreIndependentForHostAndServer", CapabilityUseTests.TwoHostCeilingsAreIndependentForHostAndServer),
    ("Capability use EveryEntryRequiresCurrentProofAndClosedInputs", CapabilityUseTests.EveryEntryRequiresCurrentProofAndClosedInputs),
    ("Capability use UseDenialsRecordRealActorTargetAndSurviveContention", CapabilityUseTests.UseDenialsRecordRealActorTargetAndSurviveContention),
    ("Capability use UseAuditFaultsAndLateProofChangesRollBack", CapabilityUseTests.UseAuditFaultsAndLateProofChangesRollBack),
    ("Permission denial EveryPreWritePathRecordsActualActorWithoutEffects", PermissionDenialAuditTests.EveryPreWritePathRecordsActualActorWithoutEffects),
    ("Permission denial AuthenticationMalformedStaleAndCancellationAreNotPolicyDenials", PermissionDenialAuditTests.AuthenticationMalformedStaleAndCancellationAreNotPolicyDenials),
    ("Permission denial DenialAuditFailureRemovalMutationAndIdentityRaceRollBack", PermissionDenialAuditTests.DenialAuditFailureRemovalMutationAndIdentityRaceRollBack),
    ("Permission denial PresetDenialAndCallbackFailureNeverCommitPartialWork", PermissionDenialAuditTests.PresetDenialAndCallbackFailureNeverCommitPartialWork),
    ("Permission denial ConcurrentDenialsKeepRevisionAndSuccessfulGrantsSeparate", PermissionDenialAuditTests.ConcurrentDenialsKeepRevisionAndSuccessfulGrantsSeparate),
    ("Permission denial LateCancellationAndSuccessfulAuditFailureDoNotBecomeDenials", PermissionDenialAuditTests.LateCancellationAndSuccessfulAuditFailureDoNotBecomeDenials),
    ("Remote grants ExactDelegationPersistsRealPeerAndProvenance", RemoteGrantPolicyTests.ExactDelegationPersistsRealPeerAndProvenance),
    ("Remote grants RootsScopeAndRightsCannotBeManufactured", RemoteGrantPolicyTests.RootsScopeAndRightsCannotBeManufactured),
    ("Remote grants AllEntryPointsRequireCurrentTransportAndRevision", RemoteGrantPolicyTests.AllEntryPointsRequireCurrentTransportAndRevision),
    ("Remote grants LateAuditMutationsRollBackSingleAndPresetWrites", RemoteGrantPolicyTests.LateAuditMutationsRollBackSingleAndPresetWrites),
    ("Remote grants PresetValidatesEveryOriginalSourceBeforeWriting", RemoteGrantPolicyTests.PresetValidatesEveryOriginalSourceBeforeWriting),
    ("Remote grants ConcurrentRemoteWritersAndExactRevocation", RemoteGrantPolicyTests.ConcurrentRemoteWritersAndExactRevocation),
    ("Remote grants EmptyCancellationAndTrustedPendingEvidence", RemoteGrantPolicyTests.EmptyCancellationAndTrustedPendingEvidence),
    ("Creator grants SixCanonicalCandidatesHaveExactScopeAndNoDelegation", CreatorGrantPolicyTests.SixCanonicalCandidatesHaveExactScopeAndNoDelegation),
    ("Creator grants ConfirmedCreationCommitsSixWithRealActorAudit", CreatorGrantPolicyTests.ConfirmedCreationCommitsSixWithRealActorAudit),
    ("Creator grants FailedMissingAndExistingCreationNeverGrant", CreatorGrantPolicyTests.FailedMissingAndExistingCreationNeverGrant),
    ("Creator grants CurrentTransportAndCreateAuthorityAreRequired", CreatorGrantPolicyTests.CurrentTransportAndCreateAuthorityAreRequired),
    ("Creator grants ConfirmationAndAuditMutationsRollBackEverything", CreatorGrantPolicyTests.ConfirmationAndAuditMutationsRollBackEverything),
    ("Creator grants ConcurrentFinalizationAndCreateRevocationStaySeparate", CreatorGrantPolicyTests.ConcurrentFinalizationAndCreateRevocationStaySeparate),
    ("Creator grants MachineGrantDoesNotAuthorizeEveryLocalUser", CreatorGrantPolicyTests.MachineGrantDoesNotAuthorizeEveryLocalUser),
    ("Creator grants PendingPinAndCancellationRespectCurrentEvidence", CreatorGrantPolicyTests.PendingPinAndCancellationRespectCurrentEvidence),
    ("Historical reissue PureRulesRequireOwnerAndHistoricalRoots", HistoricalGrantReissueTests.PureRulesRequireOwnerAndHistoricalRoots),
    ("Historical reissue NewRootsPreserveHistoryAndActualAudit", HistoricalGrantReissueTests.NewRootsPreserveHistoryAndActualAudit),
    ("Historical reissue InvalidSelectionsAndNonOwnerHaveNoEffects", HistoricalGrantReissueTests.InvalidSelectionsAndNonOwnerHaveNoEffects),
    ("Historical reissue CurrentPeerStateIsRequired", HistoricalGrantReissueTests.CurrentPeerStateIsRequired),
    ("Historical reissue LaterAuditAndAuthorityMutationRollBack", HistoricalGrantReissueTests.LaterAuditAndAuthorityMutationRollBack),
    ("Historical reissue ConcurrentSelectionAndFreshExplicitRepeat", HistoricalGrantReissueTests.ConcurrentSelectionAndFreshExplicitRepeat),
    ("Historical reissue EmptyAndCancelledCallsDoNotWrite", HistoricalGrantReissueTests.EmptyAndCancelledCallsDoNotWrite),
    ("Role presets ImmutableShapesAndPureCanonicalExpansion", RolePresetTests.ImmutableShapesAndPureCanonicalExpansion),
    ("Role presets MixedPresetPersistsExactRowsAndAudit", RolePresetTests.MixedPresetPersistsExactRowsAndAudit),
    ("Role presets AnyUnauthorizedEntryRejectsWholePreset", RolePresetTests.AnyUnauthorizedEntryRejectsWholePreset),
    ("Role presets AuditAndSourceChangesRollBackWholeBatch", RolePresetTests.AuditAndSourceChangesRollBackWholeBatch),
    ("Role presets ConcurrentPresetsOwnerRootsAndExactRevocation", RolePresetTests.ConcurrentPresetsOwnerRootsAndExactRevocation),
    ("Role presets EmptyAndCancelledPresetHaveNoEffects", RolePresetTests.EmptyAndCancelledPresetHaveNoEffects),
    ("Default grants FactoryShapesAndUpgrade", DefaultGrantPolicyTests.FactoryShapesAndUpgrade),
    ("Default grants OnlyFreshOwnerMayConfigure", DefaultGrantPolicyTests.OnlyFreshOwnerMayConfigure),
    ("Default grants CurrentActivationDefaultsAndNoRetroactivity", DefaultGrantPolicyTests.CurrentActivationDefaultsAndNoRetroactivity),
    ("Default grants ConfigurationAuditAndMutationRollback", DefaultGrantPolicyTests.ConfigurationAuditAndMutationRollback),
    ("Default grants InnerAndOuterActivationAuditRollback", DefaultGrantPolicyTests.InnerAndOuterActivationAuditRollback),
    ("Default grants ExpiryCancellationAndMissingFinalGuard", DefaultGrantPolicyTests.ExpiryCancellationAndMissingFinalGuard),
    ("Default grants ConcurrentActivationUsesIndependentGuards", DefaultGrantPolicyTests.ConcurrentActivationUsesIndependentGuards),
    ("Default grants TwoHostActivationIsIndependent", DefaultGrantPolicyTests.TwoHostActivationIsIndependent),
    ("Grant persistence CanonicalWritesAndRealAudit", GrantPolicyPersistenceTests.CanonicalWritesAndRealAudit),
    ("Grant persistence DenialsAndFreshLocalProof", GrantPolicyPersistenceTests.DenialsAndFreshLocalProof),
    ("Grant persistence ExactOwnerSubtreeAndExistingRevocation", GrantPolicyPersistenceTests.ExactOwnerSubtreeAndExistingRevocation),
    ("Grant persistence AuditFailureAndPostAuditMutationRollback", GrantPolicyPersistenceTests.AuditFailureAndPostAuditMutationRollback),
    ("Grant persistence PostAuditRevokeChangesAndLateCancellation", GrantPolicyPersistenceTests.PostAuditRevokeChangesAndLateCancellation),
    ("Grant persistence ConcurrentWritersAndActorTrustRevisions", GrantPolicyPersistenceTests.ConcurrentWritersAndActorTrustRevisions),
    ("Grant persistence UpgradePreservesGrantsAndRevisionFailsClosed", GrantPolicyPersistenceTests.UpgradePreservesGrantsAndRevisionFailsClosed),
    ("Offline recovery OfflineRecoveryAuditDeletionRollsBack", PeerTrustRevocationTests.OfflineRecoveryAuditDeletionRollsBack),
    ("Offline recovery OfflineRecoveryCarriesExactApprovalThroughRepeatedRecovery", PeerTrustRevocationTests.OfflineRecoveryCarriesExactApprovalThroughRepeatedRecovery),
    ("Offline recovery OfflineRecoveryNeverRevivesIneligibleMarkers", PeerTrustRevocationTests.OfflineRecoveryNeverRevivesIneligibleMarkers),
    ("Offline recovery OfflineRecoveryContinuityStillCancelsOnRevokeAndReplacement", PeerTrustRevocationTests.OfflineRecoveryContinuityStillCancelsOnRevokeAndReplacement),
    ("Offline recovery OfflineRecoveryLateEffectsRollBackWholeWriter", PeerTrustRevocationTests.OfflineRecoveryLateEffectsRollBackWholeWriter),
    ("Recovery receipt RecoveryReceiptClearsExactKeyWithoutNewGrants", PeerTrustRevocationTests.RecoveryReceiptClearsExactKeyWithoutNewGrants),
    ("Recovery receipt RecoveryReceiptDuplicateAndNoOpAreBounded", PeerTrustRevocationTests.RecoveryReceiptDuplicateAndNoOpAreBounded),
    ("Recovery receipt RecoveryReceiptMismatchAndSecondRecoveryCannotUnlock", PeerTrustRevocationTests.RecoveryReceiptMismatchAndSecondRecoveryCannotUnlock),
    ("Recovery receipt RecoveryReceiptRefusesChangedProofAndRelationship", PeerTrustRevocationTests.RecoveryReceiptRefusesChangedProofAndRelationship),
    ("Recovery receipt RecoveryReceiptPreservesOldAndStagedNewRotation", PeerTrustRevocationTests.RecoveryReceiptPreservesOldAndStagedNewRotation),
    ("Recovery receipt RecoveryReceiptCarriesOnlyExactOutgoingApproval", PeerTrustRevocationTests.RecoveryReceiptCarriesOnlyExactOutgoingApproval),
    ("Recovery receipt RecoveryReceiptLateEffectsAndAuditsRollback", PeerTrustRevocationTests.RecoveryReceiptLateEffectsAndAuditsRollback),
    ("Recovery receipt RecoveryReceiptConcurrencyAndCancellation", PeerTrustRevocationTests.RecoveryReceiptConcurrencyAndCancellation),
    ("Recovery receipt RecoveryReceiptSchemaAddsNoAuthority", PeerTrustRevocationTests.RecoveryReceiptSchemaAddsNoAuthority),
    ("Owner replacement OwnerReplacementAppliesCurrentDefaultsAndExactForest", PeerTrustRevocationTests.OwnerReplacementAppliesCurrentDefaultsAndExactForest),
    ("Owner replacement OwnerReplacementHandlesRevokedPeerBoundAndBenignRotation", PeerTrustRevocationTests.OwnerReplacementHandlesRevokedPeerBoundAndBenignRotation),
    ("Owner replacement OwnerReplacementRetainsRecoveryAndDeniesOrdinaryAuthority", PeerTrustRevocationTests.OwnerReplacementRetainsRecoveryAndDeniesOrdinaryAuthority),
    ("Owner replacement OwnerReplacementDeniesStaleAndUnprovenRequests", PeerTrustRevocationTests.OwnerReplacementDeniesStaleAndUnprovenRequests),
    ("Owner replacement OwnerReplacementRejectsFreshStagedCandidate", PeerTrustRevocationTests.OwnerReplacementRejectsFreshStagedCandidate),
    ("Owner replacement OwnerReplacementRetriesAndSupersedesWithoutRevival", PeerTrustRevocationTests.OwnerReplacementRetriesAndSupersedesWithoutRevival),
    ("Owner replacement OwnerReplacementLateFaultsRollback", PeerTrustRevocationTests.OwnerReplacementLateFaultsRollback),
    ("Owner replacement OwnerReplacementConcurrencyAndCancellation", PeerTrustRevocationTests.OwnerReplacementConcurrencyAndCancellation),
    ("Owner replacement ReplacementCompletionSchemaAddsNoAuthority", PeerTrustRevocationTests.ReplacementCompletionSchemaAddsNoAuthority),
    ("Replacement candidate FreshEvidenceIsDurableAndIdempotent", ReplacementCandidateTests.FreshEvidenceIsDurableAndIdempotent),
    ("Replacement candidate TrustAndStagedRotationAbaPermanentlyInvalidate", ReplacementCandidateTests.TrustAndStagedRotationAbaPermanentlyInvalidate),
    ("Replacement candidate LocalCredentialAbaPermanentlyInvalidates", ReplacementCandidateTests.LocalCredentialAbaPermanentlyInvalidates),
    ("Replacement candidate MissingOrMismatchedProofCannotBeReused", ReplacementCandidateTests.MissingOrMismatchedProofCannotBeReused),
    ("Replacement candidate CanonicalRevocationPreservesItsAtomicTimestamp", ReplacementCandidateTests.CanonicalRevocationPreservesItsAtomicTimestamp),
    ("Replacement candidate LegacyRequestsNeverGainFreshEvidence", ReplacementCandidateTests.LegacyRequestsNeverGainFreshEvidence),
    ("Replacement candidate UnrelatedAndApprovedHistoryRemainIntact", ReplacementCandidateTests.UnrelatedAndApprovedHistoryRemainIntact),
    ("Replacement candidate LateCandidateAuditAndAuthorityFaultsRollback", ReplacementCandidateTests.LateCandidateAuditAndAuthorityFaultsRollback),
    ("Unpair coordinator LocalCommitReturnsBeforeHeldNotification", LiveUnpairConnectionTests.LocalCommitReturnsBeforeHeldNotification),
    ("Unpair coordinator MissingAndPreparingConnectionsNeverDelayCommit", LiveUnpairConnectionTests.MissingAndPreparingConnectionsNeverDelayCommit),
    ("Unpair coordinator DeniedStaleCanceledCallsLeaveReadyConnectionUnused", LiveUnpairConnectionTests.DeniedStaleCanceledCallsLeaveReadyConnectionUnused),
    ("Unpair coordinator RemoteFacadeUsesSameNotificationWithoutEcho", LiveUnpairConnectionTests.RemoteFacadeUsesSameNotificationWithoutEcho),
    ("Unpair coordinator CoordinatorRejectsCrossHostWiring", LiveUnpairConnectionTests.CoordinatorRejectsCrossHostWiring),
    ("Unpair coordinator CapacityAndRemovedWorkersDrainActualCleanup", UnpairCoordinatorLifetimeTests.CapacityAndRemovedWorkersDrainActualCleanup),
    ("Unpair coordinator RetentionExpiryClosesPreparationWithoutNotice", UnpairCoordinatorLifetimeTests.RetentionExpiryClosesPreparationWithoutNotice),
    ("Unpair coordinator StopCallbackFailureStillDrainsWorkers", UnpairCoordinatorLifetimeTests.StopCallbackFailureStillDrainsWorkers),
    ("Unpair coordinator BothDispatchersRetireNotReadyNotifications", AuthenticatedPermissionDispatchTests.BothDispatchersRetireNotReadyNotifications),
    ("Unpair coordinator RegisteredUnpairPreparationStopsWithGeneration", HostNetworkGenerationTests.RegisteredUnpairPreparationStopsWithGeneration),
    ("Live unpair PreparationFeatureFailureRetainsObservedRotation", PeerRotationReceiptRpcTests.PreparationFeatureFailureRetainsObservedRotation),
    ("Live unpair CommittedUnpairGuardRequiresExactTransition", PeerTrustRevocationTests.CommittedUnpairGuardRequiresExactTransition),
    ("Live unpair ActualScopedDeliveryAndSingleAttempt", LiveUnpairConnectionTests.ActualScopedDeliveryAndSingleAttempt),
    ("Live unpair UncommittedOrLaterTransitionNeverSends", LiveUnpairConnectionTests.UncommittedOrLaterTransitionNeverSends),
    ("Live unpair LostActualTransportCannotAuthenticateAgain", LiveUnpairConnectionTests.LostActualTransportCannotAuthenticateAgain),
    ("Live unpair HeldResponseDisposalDrainsSend", LiveUnpairConnectionTests.HeldResponseDisposalDrainsSend),
    ("Live unpair PreparationRequiresActiveAndActualProof", LiveUnpairConnectionTests.PreparationRequiresActiveAndActualProof),
    ("Live unpair PreparedUnpairConnectionBelongsToGeneration", HostNetworkGenerationTests.PreparedUnpairConnectionBelongsToGeneration),
    ("Recovery pull PullActualIndependentRecoveryAndFreshConnections", RecoverySenderTests.PullActualIndependentRecoveryAndFreshConnections),
    ("Recovery pull PullReceiptValidationIsReadOnlyAndExact", RecoverySenderTests.PullReceiptValidationIsReadOnlyAndExact),
    ("Recovery pull PullLostStagesRetryOriginalReceiptAfterListenerRestart", RecoverySenderTests.PullLostStagesRetryOriginalReceiptAfterListenerRestart),
    ("Recovery pull PullChangedAuthorityAndSupersededReceiptNeverAttest", RecoverySenderTests.PullChangedAuthorityAndSupersededReceiptNeverAttest),
    ("Recovery pull PullMalformedOffersAndMismatchAudit", RecoverySenderTests.PullMalformedOffersAndMismatchAudit),
    ("Recovery pull PullMalformedConfirmationAndPostCommitChangeRefuse", RecoverySenderTests.PullMalformedConfirmationAndPostCommitChangeRefuse),
    ("Recovery pull PullNegotiationNoPendingAndInputBounds", RecoverySenderTests.PullNegotiationNoPendingAndInputBounds),
    ("Recovery pull PullGenerationStopDrainsSecondNativeCallback", RecoverySenderTests.PullGenerationStopDrainsSecondNativeCallback),
    ("Recovery pull PullSharedDeadlineSpansBothConnections", RecoverySenderTests.PullSharedDeadlineSpansBothConnections),
    ("Recovery pull PullConcurrentClientsStayIdempotent", RecoverySenderTests.PullConcurrentClientsStayIdempotent),
    ("Recovery offer OfferWireShapesAndHistory", RecoverySenderTests.OfferWireShapesAndHistory),
    ("Recovery offer OfferActualReceiptFreshConfirmationAndMutualRecovery", RecoverySenderTests.OfferActualReceiptFreshConfirmationAndMutualRecovery),
    ("Recovery offer OfferPositiveExactAttestationOnlyAndConcurrentWinner", RecoverySenderTests.OfferPositiveExactAttestationOnlyAndConcurrentWinner),
    ("Recovery offer OfferFeatureNegotiationAndNoPending", RecoverySenderTests.OfferFeatureNegotiationAndNoPending),
    ("Recovery offer OfferOriginalSessionCannotRecaptureRecoveryIncarnation", RecoverySenderTests.OfferOriginalSessionCannotRecaptureRecoveryIncarnation),
    ("Recovery offer OfferCurrentAuthorityAndLateAuditRollback", RecoverySenderTests.OfferCurrentAuthorityAndLateAuditRollback),
    ("Recovery offer OfferOwnedChannelAndCancellation", RecoverySenderTests.OfferOwnedChannelAndCancellation),
    ("Recovery offer OfferStagedKeyCannotConfirmApprovedOldKey", RecoverySenderTests.OfferStagedKeyCannotConfirmApprovedOldKey),
    ("Recovery generation composed sender and closed admission", RecoverySenderTests.GenerationComposedSenderAndClosedAdmission),
    ("Recovery generation stop drains actual native callback", RecoverySenderTests.GenerationStopDrainsActualRecoveryCallback),
    ("Recovery generation interrupted reply reopens exact approval", RecoverySenderTests.GenerationInterruptedReplyReopensExactApproval),
    ("Recovery generation requires configured serving lifetime", RecoverySenderTests.GenerationRecoveryRequiresConfiguredServingLifetime),
    ("Recovery sender ActualMutualCompletionAndFreshAuthority", RecoverySenderTests.ActualMutualCompletionAndFreshAuthority),
    ("Recovery sender ActualOwnRecoveryUsesCurrentKeyAndHistoricalApproval", RecoverySenderTests.ActualOwnRecoveryUsesCurrentKeyAndHistoricalApproval),
    ("Recovery sender UnapprovedNewOwnKeyAndOldLocalKeyRefuse", RecoverySenderTests.UnapprovedNewOwnKeyAndOldLocalKeyRefuse),
    ("Recovery sender LostReplyRetriesExactDurableApproval", RecoverySenderTests.LostReplyRetriesExactDurableApproval),
    ("Recovery sender ActualSimultaneousCompletionRequiresFreshIncarnation", RecoverySenderTests.ActualSimultaneousCompletionRequiresFreshIncarnation),
    ("Recovery sender ChangedLocalAuthorityNeverConfirms", RecoverySenderTests.ChangedLocalAuthorityNeverConfirms),
    ("Recovery sender MalformedAndMismatchRepliesNeverConfirm", RecoverySenderTests.MalformedAndMismatchRepliesNeverConfirm),
    ("Recovery sender NegotiationAndNoPendingAreBounded", RecoverySenderTests.NegotiationAndNoPendingAreBounded),
    ("Recovery sender CancellationDrainsActualNativeCallback", RecoverySenderTests.CancellationDrainsActualNativeCallback),
    ("Recovery sender ActualDeadlineDisposesHeldResponse", RecoverySenderTests.ActualDeadlineDisposesHeldResponse),
    ("Recovery sender ConcurrentNativeSendersStayIdempotent", RecoverySenderTests.ConcurrentNativeSendersStayIdempotent),
    ("Recovery confirmation ConfirmationChangesOnlyMarkerAndActualAudit", PeerTrustRevocationTests.ConfirmationChangesOnlyMarkerAndActualAudit),
    ("Recovery confirmation ConfirmationAbsentAndDuplicateAreReadOnly", PeerTrustRevocationTests.ConfirmationAbsentAndDuplicateAreReadOnly),
    ("Recovery confirmation ConfirmationUsesCurrentLocalKeyAfterOfflineRecovery", PeerTrustRevocationTests.ConfirmationUsesCurrentLocalKeyAfterOfflineRecovery),
    ("Recovery confirmation ConfirmationRechecksIncomingReceiptIncarnation", PeerTrustRevocationTests.ConfirmationRechecksIncomingReceiptIncarnation),
    ("Recovery confirmation ConfirmationRefusesStaleProofAndUnapprovedMarkers", PeerTrustRevocationTests.ConfirmationRefusesStaleProofAndUnapprovedMarkers),
    ("Recovery confirmation ConfirmationNeverSubstitutesStagedPeerKey", PeerTrustRevocationTests.ConfirmationNeverSubstitutesStagedPeerKey),
    ("Recovery confirmation ConfirmationLateEffectsAndAuditRollback", PeerTrustRevocationTests.ConfirmationLateEffectsAndAuditRollback),
    ("Recovery confirmation ConfirmationConcurrencyAndCancellation", PeerTrustRevocationTests.ConfirmationConcurrencyAndCancellation),
    ("Recovery RPC WireAndImmutableHistory", PeerRecoveryRpcTests.WireAndImmutableHistory),
    ("Recovery RPC BothRecoveryExactReceiptAndLostReplyDuplicate", PeerRecoveryRpcTests.BothRecoveryExactReceiptAndLostReplyDuplicate),
    ("Recovery RPC RecoverySessionCannotBecomeOrdinary", PeerRecoveryRpcTests.RecoverySessionCannotBecomeOrdinary),
    ("Recovery RPC ProtocolBoundsAndRecipientRefusals", PeerRecoveryRpcTests.ProtocolBoundsAndRecipientRefusals),
    ("Recovery RPC UnknownRevokedAndBoundRecoveryKeysFailTls", PeerRecoveryRpcTests.UnknownRevokedAndBoundRecoveryKeysFailTls),
    ("Recovery RPC HeldKeysAndIncarnationCannotOutliveState", PeerRecoveryRpcTests.HeldKeysAndIncarnationCannotOutliveState),
    ("Recovery RPC OldAndPendingKeyDoNotPromoteDuringRecovery", PeerRecoveryRpcTests.OldAndPendingKeyDoNotPromoteDuringRecovery),
    ("Recovery RPC AuditFaultRollsBackAndErrorsAreBounded", PeerRecoveryRpcTests.AuditFaultRollsBackAndErrorsAreBounded),
    ("Recovery RPC OwnedAdapterCancellationAndLifetime", PeerRecoveryRpcTests.OwnedAdapterCancellationAndLifetime),
    ("Unpair RPC WireAndFeatureAreClosed", PeerUnpairRpcTests.WireAndFeatureAreClosed),
    ("Unpair RPC ActualReceiptDuplicatesAndStaleHandshake", PeerUnpairRpcTests.ActualReceiptDuplicatesAndStaleHandshake),
    ("Unpair RPC ProtocolRecipientAndNonActiveRefusals", PeerUnpairRpcTests.ProtocolRecipientAndNonActiveRefusals),
    ("Unpair RPC ActualCurrentKeyAndLaterRelationshipRefuse", PeerUnpairRpcTests.ActualCurrentKeyAndLaterRelationshipRefuse),
    ("Unpair RPC ActualAuditFaultIsAtomicAndBounded", PeerUnpairRpcTests.ActualAuditFaultIsAtomicAndBounded),
    ("Unpair RPC AdapterConnectionLifetimeAndRuntimeRefusals", PeerUnpairRpcTests.AdapterConnectionLifetimeAndRuntimeRefusals),
    ("Reciprocal unpair ReciprocalNoticeIsSelfOnlyAndAtomic", PeerTrustRevocationTests.ReciprocalNoticeIsSelfOnlyAndAtomic),
    ("Reciprocal unpair ReciprocalDuplicatePreservesNewPendingIntent", PeerTrustRevocationTests.ReciprocalDuplicatePreservesNewPendingIntent),
    ("Reciprocal unpair ReciprocalReceiptNeverRestoresOrdinaryOrLaterTrust", PeerTrustRevocationTests.ReciprocalReceiptNeverRestoresOrdinaryOrLaterTrust),
    ("Reciprocal unpair ReciprocalReceiptAndAuditFaultsRollback", PeerTrustRevocationTests.ReciprocalReceiptAndAuditFaultsRollback),
    ("Reciprocal unpair ReciprocalConcurrencyAndLateCancellation", PeerTrustRevocationTests.ReciprocalConcurrencyAndLateCancellation),
    ("Reciprocal unpair ReciprocalSchemaUpgradeAndIncarnationCascade", PeerTrustRevocationTests.ReciprocalSchemaUpgradeAndIncarnationCascade),
    ("Reciprocal unpair ReciprocalNonActiveAndAbsentEvidenceRefuse", PeerTrustRevocationTests.ReciprocalNonActiveAndAbsentEvidenceRefuse),
    ("Remote trust revocation RemoteAdministratorTargetsOnlyItsSelectedPeer", PeerTrustRevocationTests.RemoteAdministratorTargetsOnlyItsSelectedPeer),
    ("Remote trust revocation RemoteSelfRevocationCannotReuseItsOldProof", PeerTrustRevocationTests.RemoteSelfRevocationCannotReuseItsOldProof),
    ("Remote trust revocation RemoteProofAndExactCapabilityRefusals", PeerTrustRevocationTests.RemoteProofAndExactCapabilityRefusals),
    ("Remote trust revocation RemoteLateIdentityAndAuditEffectsRollback", PeerTrustRevocationTests.RemoteLateIdentityAndAuditEffectsRollback),
    ("Remote trust revocation RemoteRevocationMayRemoveItsOwnDelegatedCapability", PeerTrustRevocationTests.RemoteRevocationMayRemoveItsOwnDelegatedCapability),
    ("Remote trust revocation LocalRevocationDispatchBindsActorAndLifetime", AuthenticatedPermissionDispatchTests.LocalRevocationDispatchBindsActorAndLifetime),
    ("Remote trust revocation RemoteRevocationDispatchBindsOriginalProof", AuthenticatedPermissionDispatchTests.RemoteRevocationDispatchBindsOriginalProof),
    ("Remote trust revocation RevocationAfterStagedPromotionNeedsFreshRevision", AuthenticatedPermissionDispatchTests.RevocationAfterStagedPromotionNeedsFreshRevision),
    ("Local trust revocation AtomicForestsTombstoneAndAudit", PeerTrustRevocationTests.AtomicForestsTombstoneAndAudit),
    ("Local trust revocation LocalCapabilityAndIdentityBoundaries", PeerTrustRevocationTests.LocalCapabilityAndIdentityBoundaries),
    ("Local trust revocation IdempotenceAndFreshPairingGate", PeerTrustRevocationTests.IdempotenceAndFreshPairingGate),
    ("Local trust revocation StaleRequestAndCancellationRollback", PeerTrustRevocationTests.StaleRequestAndCancellationRollback),
    ("Local trust revocation EveryLateEffectAndAuditFailureRollsBack", PeerTrustRevocationTests.EveryLateEffectAndAuditFailureRollsBack),
    ("Local trust revocation ConcurrentRevocationsHaveOneWinner", PeerTrustRevocationTests.ConcurrentRevocationsHaveOneWinner),
    ("Local trust revocation HistoricalInvalidationRemainsPermanent", PeerTrustRevocationTests.HistoricalInvalidationRemainsPermanent),
    ("Local trust revocation PeerBoundAndRecoveryStatesAreRevocable", PeerTrustRevocationTests.PeerBoundAndRecoveryStatesAreRevocable),
    ("Cross-Host operations DestinationOwnershipVisibilityAndDisconnectAcrossScopes", OperationCrossHostTests.DestinationOwnershipVisibilityAndDisconnectAcrossScopes),
    ("Host operation lifetime BootstrapThenConcurrentReadyUsesOneRuntime", HostOperationLifetimeTests.BootstrapThenConcurrentReadyUsesOneRuntime),
    ("Host operation lifetime EmptyProductionRegistryPreservesEveryTarget", HostOperationLifetimeTests.EmptyProductionRegistryPreservesEveryTarget),
    ("Host operation lifetime FailedInitializationNeverRetriesInSameOwner", HostOperationLifetimeTests.FailedInitializationNeverRetriesInSameOwner),
    ("Host operation lifetime ShutdownRacingReadyDrainsActualWork", HostOperationLifetimeTests.ShutdownRacingReadyDrainsActualWork),
    ("Host operation lifetime EnclosingLeaseOutlivesWorkerDrain", HostOperationLifetimeTests.EnclosingLeaseOutlivesWorkerDrain),
    ("Operation Activity TargetsScopesAndIndependentReaders", OperationActivityTests.TargetsScopesAndIndependentReaders),
    ("Operation Activity MalformedAndUnknownWireValuesRefuse", OperationActivityTests.MalformedAndUnknownWireValuesRefuse),
    ("Operation Activity RuntimeStatusChangesWithoutDurableRevision", OperationActivityTests.RuntimeStatusChangesWithoutDurableRevision),
    ("Operation Activity InconsistentHistoryAndOrphanLocksRemainVisible", OperationActivityTests.InconsistentHistoryAndOrphanLocksRemainVisible),
    ("Operation Activity NegotiationAndEveryObservationValue", OperationActivityTests.NegotiationAndEveryObservationValue),
    ("Host operation recovery PreparationRequiresExactPolicyAndKeepsIdentityAndLock", HostOperationRecoveryTests.PreparationRequiresExactPolicyAndKeepsIdentityAndLock),
    ("Host operation recovery PreparationFaultsRollBackAndRetry", HostOperationRecoveryTests.PreparationFaultsRollBackAndRetry),
    ("Host operation recovery StartupUsesOnlyExplicitHandlersOnBothTargets", HostOperationRecoveryTests.StartupUsesOnlyExplicitHandlersOnBothTargets),
    ("Host operation recovery DiscardCleansBeforeReleasingEitherScope", HostOperationRecoveryTests.DiscardCleansBeforeReleasingEitherScope),
    ("Host operation recovery MissingChangedManualAndDamagedStateNeverDispatch", HostOperationRecoveryTests.MissingChangedManualAndDamagedStateNeverDispatch),
    ("Host operation recovery ConcurrentInitializationAndFailureNeverLoop", HostOperationRecoveryTests.ConcurrentInitializationAndFailureNeverLoop),
    ("Host operation recovery RecoveryShutdownDrainsAndKeepsPreparedState", HostOperationRecoveryTests.RecoveryShutdownDrainsAndKeepsPreparedState),
    ("Host operation recovery LateAuthorityRefusalSkipsOnlyThatWorker", HostOperationRecoveryTests.LateAuthorityRefusalSkipsOnlyThatWorker),
    ("Host operation worker DisconnectAndWaitCancellationDoNotCancelWork", HostOperationWorkerTests.DisconnectAndWaitCancellationDoNotCancelWork),
    ("Host operation worker ShutdownDrainsWorkersAndRetainsUnfinishedState", HostOperationWorkerTests.ShutdownDrainsWorkersAndRetainsUnfinishedState),
    ("Host operation worker FailedAndUnfinishedWorkersNeverReleaseLocks", HostOperationWorkerTests.FailedAndUnfinishedWorkersNeverReleaseLocks),
    ("Host operation worker FailedAndContendingAdmissionsDispatchExactlyOnce", HostOperationWorkerTests.FailedAndContendingAdmissionsDispatchExactlyOnce),
    ("Host operation worker WorkerDoesNotInheritAmbientRequestContext", HostOperationWorkerTests.WorkerDoesNotInheritAmbientRequestContext),
    ("Host operation worker StartupInspectsEveryTargetWithoutImplicitDispatch", HostOperationWorkerTests.StartupInspectsEveryTargetWithoutImplicitDispatch),
    ("Host operation worker StaleExternalTransitionDoesNotOverwriteOrRelease", HostOperationWorkerTests.StaleExternalTransitionDoesNotOverwriteOrRelease),
    ("Host operation worker ThrowingShutdownCallbackStillDrains", HostOperationWorkerTests.ThrowingShutdownCallbackStillDrains),
    ("Host operation worker ConcurrentShutdownCannotLoseAnAdmittedWorker", HostOperationWorkerTests.ConcurrentShutdownCannotLoseAnAdmittedWorker),
    ("Operation lifecycle FingerprintsAndQualifiedTargets", OperationLifecycleTests.FingerprintsAndQualifiedTargets),
    ("Operation lifecycle ConflictMatrixAndReadOnlyVisibility", OperationLifecycleTests.ConflictMatrixAndReadOnlyVisibility),
    ("Operation lifecycle ContendingAdmissionAndStaleTransitions", OperationLifecycleTests.ContendingAdmissionAndStaleTransitions),
    ("Operation lifecycle FaultMatricesRollBackAndRetry", OperationLifecycleTests.FaultMatricesRollBackAndRetry),
    ("Operation lifecycle AuthorityCancellationAndClosedInputs", OperationLifecycleTests.AuthorityCancellationAndClosedInputs),
    ("Operation lifecycle HistoricalAndChangedDefinitionsRequireRecovery", OperationLifecycleTests.HistoricalAndChangedDefinitionsRequireRecovery),
    ("Operation lifecycle RevisionIntegrityAndCollateralMutation", OperationLifecycleTests.RevisionIntegrityAndCollateralMutation),
    ("Operation lifecycle RealProcessTerminationPreservesAtomicPairs", OperationCrashTests.RealProcessTerminationPreservesAtomicPairs),
    ("Resource revision QualifiedResourcesRemainIndependentAfterReopen", ConfigurationRevisionTests.QualifiedResourcesRemainIndependentAfterReopen),
    ("Resource revision ConcurrentWritersRejectStaleBeforeAction", ConfigurationRevisionTests.ConcurrentWritersRejectStaleBeforeAction),
    ("Resource revision ActionAndFinalValidationFailuresRollBack", ConfigurationRevisionTests.ActionAndFinalValidationFailuresRollBack),
    ("Resource revision RevisionTriggerFaultsCannotCommitPartialData", ConfigurationRevisionTests.RevisionTriggerFaultsCannotCommitPartialData),
    ("Resource revision InvalidStorageHostAndCancellationNeverRunAction", ConfigurationRevisionTests.InvalidStorageHostAndCancellationNeverRunAction),
    ("Resource revision ReadersSeeCommittedStateAndLateHostChangesRollBack", ConfigurationRevisionTests.ReadersSeeCommittedStateAndLateHostChangesRollBack),
    ("Operations FixedConflictHierarchyAndIndependentTargets", OperationDefinitionTests.FixedConflictHierarchyAndIndependentTargets),
    ("Operations ClosedIdentityAndLockInputs", OperationDefinitionTests.ClosedIdentityAndLockInputs),
    ("Operations ExplicitPerKindRecoveryAndTransitions", OperationDefinitionTests.ExplicitPerKindRecoveryAndTransitions),
    ("Operations InvalidDefinitionsAndCallerMutationCannotSupplyFallbacks", OperationDefinitionTests.InvalidDefinitionsAndCallerMutationCannotSupplyFallbacks),
    ("Authorization ModelsAndProtocolMapping", AuthorizationPolicyTests.ModelsAndProtocolMapping),
    ("Authorization ExhaustiveDelegationRightsAndTypedScope", AuthorizationPolicyTests.ExhaustiveDelegationRightsAndTypedScope),
    ("Authorization ForestValidityAndExactSubtrees", AuthorizationPolicyTests.ForestValidityAndExactSubtrees),
    ("Authorization MalformedLineagesAndStructuralOwner", AuthorizationPolicyTests.MalformedLineagesAndStructuralOwner),
    ("Authorization DualLocalAndRemoteCeilings", AuthorizationPolicyTests.DualLocalAndRemoteCeilings),
    ("Authorization SnapshotIsolationAndClosedInputs", AuthorizationPolicyTests.SnapshotIsolationAndClosedInputs),
    ("Retirement failures and final audit retry before terminal rotation completion", RotationCompletionTests.DeletionFailureAndCompletionAuditRecover),
    ("Retirement schema upgrade preserves intent without inventing deletion", RotationCompletionTests.UpgradePreservesIntentWithoutInventingDeletion),
    ("Offline recovery supersedes retirement intent and final audit changes roll back", RotationCompletionTests.RecoverySupersedesIntentAndFinalAuditRollsBack),
    ("Rotation completion EveryPeerMustResolveAndExpiryIsNotProof", RotationCompletionTests.EveryPeerMustResolveAndExpiryIsNotProof),
    ("Rotation completion CurrentIncarnationAndFirstNewProof", RotationCompletionTests.CurrentIncarnationAndFirstNewProof),
    ("Rotation completion FinalAuditAndScopeRollback", RotationCompletionTests.FinalAuditAndScopeRollback),
    ("Rotation completion WriterQueueRechecksOwnerPeerAndCancellation", RotationCompletionTests.WriterQueueRechecksOwnerPeerAndCancellation),
    ("Rotation completion InvalidMetadataAndCompletedScopeRefuse", RotationCompletionTests.InvalidMetadataAndCompletedScopeRefuse),
    ("Local binding ConservativeUpgradeAndRollback", PeerLocalBindingEvidenceTests.ConservativeUpgradeAndRollback),
    ("Local binding OwnerBindingContinuityAndInvalidation", PeerLocalBindingEvidenceTests.OwnerBindingContinuityAndInvalidation),
    ("Local binding AtomicAuditAndFinalContextRollback", PeerLocalBindingEvidenceTests.AtomicAuditAndFinalContextRollback),
    ("Local binding QueuedOwnerChangeAndFirstNewBinding", PeerLocalBindingEvidenceTests.QueuedOwnerChangeAndFirstNewBinding),
    ("Current credential confirmation upgrade cancellation and first-New pairing", PeerRelationshipIncarnationTests.CurrentConfirmationUpgradeCancellationAndFirstNewPairing),
    ("Current credential confirmation repairs cleared legacy evidence", PeerRotationReceiptRpcTests.CurrentConfirmationRepairsClearedLegacyEvidence),
    ("Current credential confirmation does not invent history or clear receipts", PeerRotationReceiptRpcTests.CurrentConfirmationDoesNotInventHistoryOrClearReceipt),
    ("Current credential confirmation protocol and trust refusal", PeerRotationReceiptRpcTests.CurrentConfirmationProtocolAndTrustRefusals),
    ("Current credential confirmation denies Old and unobserved Pending", PeerRotationReceiptRpcTests.CurrentConfirmationRejectsOldAndUnobservedPending),
    ("Current credential confirmation reply faults and state retry", PeerRotationReceiptRpcTests.CurrentConfirmationReplyFaultsAndStateChangeRetry),
    ("Current credential confirmation audit and queued writer rollback", PeerRotationReceiptRpcTests.CurrentConfirmationAuditAndQueuedWriterRollback),
    ("Relationship provenance conservative migration", PeerRelationshipIncarnationTests.ConservativeUpgradeAndMigrationRollback),
    ("Relationship provenance activation and routine continuity", PeerRelationshipIncarnationTests.ActivationRoutinePromotionAndUnrelatedPeersPreserveEvidence),
    ("Relationship provenance identical-state ABA and pairing changes", PeerRelationshipIncarnationTests.SameIdentityAbaAndPairingChangesInvalidate),
    ("Relationship provenance queued receipt rollback and metadata refusal", PeerRelationshipIncarnationTests.QueuedReceiptAuditRollbackAndMetadataRefusal),
    ("Relationship provenance real negotiated-session ABA", PeerRotationReceiptRpcTests.NegotiatedRelationshipCannotSurviveIdenticalStateAba),
    ("Local Owner activation ExactOwnerAndIdempotentActivation", LocalOwnerActivationTests.ExactOwnerAndIdempotentActivation),
    ("Local Owner activation HookAndCancellationRollback", LocalOwnerActivationTests.HookAndCancellationRollback),
    ("Local Owner activation FinalWriterFreshness", LocalOwnerActivationTests.FinalWriterFreshness),
    ("Local Owner activation RPC boundaries", LocalOwnerPairingRpcTests.ActivationBoundaries),
    ("Local Owner RPC CapabilityAndNativeIdentity", LocalOwnerPairingRpcTests.CapabilityAndNativeIdentity),
    ("Local Owner RPC DiscoveryAndRequestBounds", LocalOwnerPairingRpcTests.DiscoveryAndRequestBounds),
    ("Local Owner RPC StaleOwnerAndResponseConstruction", LocalOwnerPairingRpcTests.StaleOwnerAndResponseConstruction),
    ("Local Owner pairing RepositoryBoundaries", LocalOwnerPairingTests.RepositoryBoundaries),
    ("Local Owner pairing WriterQueueFreshness", LocalOwnerPairingTests.WriterQueueFreshness),
    ("Local Owner pairing AuthenticatedGenerationActions", LocalOwnerPairingTests.AuthenticatedGenerationActions),
    ("Local Owner pairing UnreturnedInvitationCleanup", LocalOwnerPairingTests.UnreturnedInvitationCleanup),
    ("Generation discovery ActualBoundMetadataAndSealedConfiguration", HostGenerationDiscoveryTests.ActualBoundMetadataAndSealedConfiguration),
    ("Generation discovery StopStartsDiscoveryBeforeTrafficDrain", HostGenerationDiscoveryTests.StopStartsDiscoveryBeforeTrafficDrain),
    ("Generation discovery StartupCancellationOwnsReturnedDiscovery", HostGenerationDiscoveryTests.StartupCancellationOwnsReturnedDiscovery),
    ("Generation discovery StartupFailureAndUnavailableManualService", HostGenerationDiscoveryTests.StartupFailureAndUnavailableManualService),
    ("Generation discovery FatalCompletionAndCleanupRefuseCutover", HostGenerationDiscoveryTests.FatalCompletionAndCleanupRefuseCutover),
    ("Host discovery runtime StartupMetadataAndExpiry", HostDiscoveryRuntimeTests.StartupMetadataAndExpiry),
    ("Host discovery runtime BoundedRetriesAndFreshReceiver", HostDiscoveryRuntimeTests.BoundedRetriesAndFreshReceiver),
    ("Host discovery runtime StopDrainsEveryOwnedActivity", HostDiscoveryRuntimeTests.StopDrainsEveryOwnedActivity),
    ("Host discovery runtime FatalFailuresNeverRetryOrHideCleanup", HostDiscoveryRuntimeTests.FatalFailuresNeverRetryOrHideCleanup),
    ("Host discovery runtime SenderFailureDrainsReceiverAndCancellation", HostDiscoveryRuntimeTests.SenderFailureDrainsReceiverAndCancellation),
    ("Host discovery runtime ActualLoopbackPacketsAndRelease", HostDiscoveryRuntimeTests.ActualLoopbackPacketsAndRelease),
    ("Manual Host addresses reject authority and URI injection", HostDiscoveryTests.ManualAddresses),
    ("Host discovery packets remain bounded and unverified across versions", HostDiscoveryTests.PacketBoundsAndCompatibility),
    ("Host discovery directory preserves actual source conflicts and monotonic expiry", HostDiscoveryTests.DirectorySourceBoundsAndExpiry),
    ("Address/code selects only the latest live invitation without fallback", PairingAttemptTests.AdvertisedSelection),
    ("Advertised invitation selection and admission serialize with creation and cancellation", PairingAttemptTests.AdvertisedAdmissionIsAtomic),
    ("Shell uses exact Host-qualified identity and separates focus from selection", ShellStateTests.ExactIdentityAndFocus),
    ("Shell inventory changes preserve aliases and remove hidden targets", ShellStateTests.InventoryAndAliases),
    ("Shell rejects stale, denied and canceled selection replies", ShellStateTests.StaleSelection),
    ("Shell semantic themes preserve contrast, layout and reduce-motion rules", ShellStateTests.TokensAndResponsiveRules),
    ("Ordinary client bootstrap lost reply preserves prepared key", ClientSecurityCompositionTests.BootstrapLostReply),
    ("Ordinary client rotation confirms durable binding before handoff deletion", ClientSecurityCompositionTests.RotationBindingAndDeletion),
    ("Ordinary client re-home preserves persisted key choice across lost results", ClientSecurityCompositionTests.RehomeKeyChoice),
    ("Ordinary client enrollment CLI and Owner authority use real local RPC", ClientSecurityCompositionTests.EnrollmentAndAuthority),
    ("Ordinary client activation and errors preserve security classifications", ClientSecurityCompositionTests.ActivationAndErrors),
    ("Local connect exposes only authenticated semantic Host and principal identity", ClientSecurityCompositionTests.ConnectionIdentity),
    ("Ordinary client negotiation and prepared-key proof fail closed", ClientSecurityCompositionTests.NegotiationAndProofBoundaries),
    ("Host service worker releases resources and distinguishes stop from failure", HostServiceWorkerTests.Lifecycle),
    ("Production local listener ignores external hosting and endpoint configuration", LocalSecurityRpcTests.ProductionConfiguration),
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
