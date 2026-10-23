namespace DbBackupManager.Domain.BackupLogs;

/// <summary>
/// LOG 领域规则的稳定原因码。原因码只表达领域事实，不是调用授权。
/// </summary>
public static class LogReason
{
    public const string TechnicalReady = "log.technical_ready";
    public const string ObservationMissing = "log.observation_missing";
    public const string ObservationPermissionDenied = "log.observation_permission_denied";
    public const string ObservationConflict = "log.observation_conflict";
    public const string PlatformDatabaseMissing = "log.platform_database_missing";
    public const string DatabaseOffline = "log.database_offline";
    public const string RecoveryModelNotFull = "log.recovery_model_not_full";
    public const string RecoveryModelUnknown = "log.recovery_model_unknown";
    public const string CapabilityUnsupported = "log.capability_unsupported";
    public const string CapabilityUnknown = "log.capability_unknown";
    public const string IdentityUnknown = "log.identity_unknown";
    public const string IdentityMismatch = "log.identity_mismatch";
    public const string BranchUnknown = "log.branch_unknown";
    public const string ChainNotReady = "log.chain_not_ready";
    public const string ResourceBusy = "log.resource_busy";
    public const string ResourceBudgetUnavailable = "log.resource_budget_unavailable";
    public const string ResourceUnknown = "log.resource_unknown";

    public const string PriorCallActive = "log.prior_call_active";
    public const string PriorCallTerminationUnknown = "log.prior_call_termination_unknown";
    public const string PriorPermitOutstanding = "log.prior_permit_outstanding";
    public const string OriginalSlotNotReplayable = "log.original_slot_not_replayable";
    public const string FutureRequestIndependent = "log.future_request_independent";
    public const string PriorCallTerminated = "log.prior_call_terminated";
    public const string NoPriorCall = "log.no_prior_call";

    public const string StartManagedFull = "log.start_managed_full";
    public const string StartManagedDifferential = "log.start_managed_differential";
    public const string StartNoManagedFull = "log.start_no_managed_full";
    public const string StartLocationMissing = "log.start_location_missing";
    public const string StartCopyOnlyNotEligible = "log.start_copy_only_not_eligible";
    public const string StartExternalNotManaged = "log.start_external_not_managed";
    public const string StartEvidenceUnknown = "log.start_evidence_unknown";
    public const string StartIdentityConflict = "log.start_identity_conflict";
    public const string StartBackupInvalid = "log.start_backup_invalid";
    public const string StartNotAvailable = "log.start_not_available";
    public const string StartBranchUnsupported = "log.start_branch_unsupported";
    public const string StartIdentityUnsupported = "log.start_identity_unsupported";
    public const string StartGenerationUnsupported = "log.start_generation_unsupported";

    public const string CoverageContinuous = "log.coverage_continuous";
    public const string CoverageGap = "log.coverage_gap";
    public const string CoverageUnknown = "log.coverage_unknown";
    public const string CoverageBranchUnsupported = "log.coverage_branch_unsupported";
    public const string IntervalMissingFields = "log.interval_missing_fields";
    public const string IntervalInvalid = "log.interval_invalid";
    public const string QueryIncomplete = "log.query_incomplete";
    public const string DuplicateIdentityConflict = "log.duplicate_identity_conflict";
    public const string LocationMismatch = "log.location_mismatch";
    public const string ExternalNotManaged = "log.external_not_managed";
    public const string CopyMissing = "log.copy_missing";
    public const string CopyUnknown = "log.copy_unknown";
    public const string ActualSourceConflict = "log.actual_source_conflict";
    public const string ActualEvidenceUnknown = "log.actual_evidence_unknown";
    public const string ActualTypeUnknown = "log.actual_type_unknown";
    public const string ActualTypeNotLog = "log.actual_type_not_log";
    public const string ActualCopyOnlyUnknown = "log.actual_copy_only_unknown";
    public const string ActualCopyOnly = "log.actual_copy_only";
    public const string ActualRecoveryModelUnknown = "log.actual_recovery_model_unknown";
    public const string ActualRecoveryModelNotFull = "log.actual_recovery_model_not_full";
    public const string ActualBulkUnknown = "log.actual_bulk_unknown";
    public const string ActualBulkLogged = "log.actual_bulk_logged";
    public const string ActualForceOfflineUnknown = "log.actual_force_offline_unknown";
    public const string ActualForceOffline = "log.actual_force_offline";
    public const string ActualMetadataUnknown = "log.actual_metadata_unknown";
    public const string ActualMetadataInvalid = "log.actual_metadata_invalid";
    public const string ActualProtectionUnknown = "log.actual_protection_unknown";
    public const string ActualProtectionMismatch = "log.actual_protection_mismatch";
    public const string ActualIdentityUnknown = "log.actual_identity_unknown";
    public const string ActualIdentityUnsupported = "log.actual_identity_unsupported";
    public const string ActualBranchUnknown = "log.actual_branch_unknown";
    public const string ActualBranchUnsupported = "log.actual_branch_unsupported";
    public const string ActualGenerationUnknown = "log.actual_generation_unknown";
    public const string ActualGenerationUnsupported = "log.actual_generation_unsupported";

    public const string ScheduleSelected = "log.schedule_selected";
    public const string ScheduleWaiting = "log.schedule_waiting";
    public const string SchedulePaused = "log.schedule_paused";
    public const string ScheduleNoLogSchedule = "log.schedule_no_log";
    public const string ScheduleClockRollback = "log.schedule_clock_rollback";
    public const string ScheduleOverflow = "log.schedule_overflow";
    public const string ScheduleResumeInFuture = "log.schedule_resume_in_future";

    public const string MergeEligible = "log.merge_eligible";
    public const string MergeNotLog = "log.merge_not_log";
    public const string MergeManual = "log.merge_manual";
    public const string MergeEnded = "log.merge_ended";
    public const string MergeAttemptExists = "log.merge_attempt_exists";
    public const string MergeReservationExists = "log.merge_reservation_exists";
    public const string MergePermitExists = "log.merge_permit_exists";
    public const string MergeSideEffectExists = "log.merge_side_effect_exists";

    public const string AgeOk = "log.age_ok";
    public const string AgeExceeded = "log.age_exceeded";
    public const string AgePaused = "log.age_paused";
    public const string AgeNoSuccess = "log.age_no_success";

    public const string BudgetConfigMissing = "log.budget_config_missing";
    public const string BudgetConfigInvalid = "log.budget_config_invalid";
    public const string BudgetObservationUnknown = "log.budget_observation_unknown";
    public const string BudgetReservationUnknown = "log.budget_reservation_unknown";
    public const string BudgetFileCountExceeded = "log.budget_file_count_exceeded";
    public const string BudgetBytesExceeded = "log.budget_bytes_exceeded";
    public const string BudgetFreeSpaceLow = "log.budget_free_space_low";
    public const string BudgetAdmissible = "log.budget_admissible";
}
