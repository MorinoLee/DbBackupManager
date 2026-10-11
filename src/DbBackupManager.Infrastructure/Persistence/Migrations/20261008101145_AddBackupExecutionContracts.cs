using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbBackupManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupExecutionContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AdmissionObservedAtUtc",
                table: "BackupAttempts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AdmissionRecoveryForkId",
                table: "BackupAttempts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AdmittedFullBackupSetId",
                table: "BackupAttempts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExpectedDatabaseGuid",
                table: "BackupAttempts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExpectedFamilyGuid",
                table: "BackupAttempts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BackupPlanExecutionOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    IntendedBackupSetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ActualBaseBackupSetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EvidenceAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    OriginalCallTerminated = table.Column<bool>(type: "bit", nullable: false),
                    OriginalCallerCannotInvoke = table.Column<bool>(type: "bit", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PlatformCompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SqlOutcomeSource = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SqlSuccessObserved = table.Column<bool>(type: "bit", nullable: false),
                    UsedChecksum = table.Column<bool>(type: "bit", nullable: true),
                    UsedCompression = table.Column<bool>(type: "bit", nullable: true),
                    UsedCopyOnly = table.Column<bool>(type: "bit", nullable: true),
                    ActiveConclusion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ActiveReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ActiveAssessmentState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Conclusion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    BaselineReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AssessmentState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletionReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CompletionSource = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ContentDigestAlgorithm = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: true),
                    ContentDigest = table.Column<byte[]>(type: "binary(32)", nullable: true),
                    ContentLengthBytes = table.Column<long>(type: "bigint", nullable: true),
                    ObjectProtection = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ContentReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StableObjectId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ContentState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BackupSetGuidState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BackupSetGuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CheckpointLsnState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CheckpointLsn = table.Column<decimal>(type: "numeric(25,0)", nullable: true),
                    DatabaseBackupLsnState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DatabaseBackupLsn = table.Column<decimal>(type: "numeric(25,0)", nullable: true),
                    DatabaseGuidState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DatabaseGuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DifferentialBaseGuidState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DifferentialBaseGuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DifferentialBaseLsnState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DifferentialBaseLsn = table.Column<decimal>(type: "numeric(25,0)", nullable: true),
                    FamilyGuidState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FamilyGuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FirstLsnState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FirstLsn = table.Column<decimal>(type: "numeric(25,0)", nullable: true),
                    FirstRecoveryForkIdState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FirstRecoveryForkId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ForkPointLsnState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ForkPointLsn = table.Column<decimal>(type: "numeric(25,0)", nullable: true),
                    HasBackupChecksumsState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    HasBackupChecksums = table.Column<bool>(type: "bit", nullable: true),
                    HasIncompleteMetadataState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    HasIncompleteMetadata = table.Column<bool>(type: "bit", nullable: true),
                    IsCompressedState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsCompressed = table.Column<bool>(type: "bit", nullable: true),
                    IsCopyOnlyState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsCopyOnly = table.Column<bool>(type: "bit", nullable: true),
                    IsDamagedState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsDamaged = table.Column<bool>(type: "bit", nullable: true),
                    IsSnapshotState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsSnapshot = table.Column<bool>(type: "bit", nullable: true),
                    LastLsnState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LastLsn = table.Column<decimal>(type: "numeric(25,0)", nullable: true),
                    RecoveryForkIdState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RecoveryForkId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SqlFinishedLocalState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SqlFinishedLocal = table.Column<DateTime>(type: "datetime2(7)", nullable: true),
                    SqlStartedLocalState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SqlStartedLocal = table.Column<DateTime>(type: "datetime2(7)", nullable: true),
                    TypeState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupPlanExecutionOperations", x => x.Id);
                    table.UniqueConstraint("AK_BackupPlanExecutionOperations_TaskId_AttemptId_Id", x => new { x.TaskId, x.AttemptId, x.Id });
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_ActiveAssessment", "([ActiveAssessmentState] = 'NotApplicable' AND [ActiveConclusion] IS NULL AND [ActiveReasonCode] IS NULL) OR ([ActiveAssessmentState] = 'Known' AND [ActiveConclusion] IS NOT NULL AND [ActiveConclusion] IN ('Verified','Unmanaged','Mismatch','Unknown') AND [ActiveReasonCode] IS NOT NULL AND [ActiveReasonCode] IN ('baseline.backup_type_mismatch','baseline.copy_only_differential','baseline.copy_only_full','baseline.database_backup_lsn_mismatch','baseline.database_identity_mismatch','baseline.duplicate_managed_guid','baseline.external_full_observed','baseline.history_not_found','baseline.lsn_mismatch','baseline.managed_full_not_found','baseline.managed_full_verified','baseline.missing_fields','baseline.multiple_bases','baseline.permission_denied','baseline.recovery_branch_mismatch','baseline.source_conflict'))");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_Assessment", "([AssessmentState] = 'NotApplicable' AND [Conclusion] IS NULL AND [BaselineReasonCode] IS NULL) OR ([AssessmentState] = 'Known' AND [Conclusion] IS NOT NULL AND [Conclusion] IN ('Verified','Unmanaged','Mismatch','Unknown') AND [BaselineReasonCode] IS NOT NULL AND [BaselineReasonCode] IN ('baseline.backup_type_mismatch','baseline.copy_only_differential','baseline.copy_only_full','baseline.database_backup_lsn_mismatch','baseline.database_identity_mismatch','baseline.duplicate_managed_guid','baseline.external_full_observed','baseline.history_not_found','baseline.lsn_mismatch','baseline.managed_full_not_found','baseline.managed_full_verified','baseline.missing_fields','baseline.multiple_bases','baseline.permission_denied','baseline.recovery_branch_mismatch','baseline.source_conflict'))");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_BackupSetGuid", "([BackupSetGuidState] = 'Known' AND [BackupSetGuid] IS NOT NULL AND [BackupSetGuid] <> '00000000-0000-0000-0000-000000000000') OR ([BackupSetGuidState] IN ('Unknown') AND [BackupSetGuid] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_CheckpointLsn", "([CheckpointLsnState] = 'Known' AND [CheckpointLsn] IS NOT NULL AND [CheckpointLsn] >= 0) OR ([CheckpointLsnState] IN ('Unknown') AND [CheckpointLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_Completion", "([CompletionSource] = 'PlatformObserved' AND [CompletedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[CompletedAtUtc]) = 0 AND [CompletionReasonCode] = 'completion.platform_observed') OR ([CompletionSource] = 'SqlLocalTime' AND [CompletedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[CompletedAtUtc]) = 0 AND [CompletionReasonCode] = 'completion.sql_local_converted') OR ([CompletionSource] = 'Unknown' AND [CompletedAtUtc] IS NULL AND [CompletionReasonCode] IN ('completion.ambiguous_local_time','completion.conversion_out_of_range','completion.future_sql_time','completion.invalid_local_time','completion.older_than_24_hours','completion.server_offset_mismatch','completion.server_offset_unknown','completion.server_time_zone_unknown','completion.sql_finish_missing'))");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_Content", "([ContentState] = 'Unknown' AND [ContentDigestAlgorithm] IS NULL AND [ContentDigest] IS NULL AND [ContentLengthBytes] IS NULL) OR ([ContentState] = 'Verified' AND [ContentDigestAlgorithm] IS NOT NULL AND [ContentDigestAlgorithm] = 'SHA256' AND [ContentDigest] IS NOT NULL AND [ContentLengthBytes] IS NOT NULL AND [ContentLengthBytes] > 0)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_DatabaseBackupLsn", "([DatabaseBackupLsnState] = 'Known' AND [DatabaseBackupLsn] IS NOT NULL AND [DatabaseBackupLsn] >= 0) OR ([DatabaseBackupLsnState] IN ('Unknown') AND [DatabaseBackupLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_DatabaseGuid", "([DatabaseGuidState] = 'Known' AND [DatabaseGuid] IS NOT NULL AND [DatabaseGuid] <> '00000000-0000-0000-0000-000000000000') OR ([DatabaseGuidState] IN ('Unknown') AND [DatabaseGuid] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_DifferentialBaseGuid", "([DifferentialBaseGuidState] = 'Known' AND [DifferentialBaseGuid] IS NOT NULL AND [DifferentialBaseGuid] <> '00000000-0000-0000-0000-000000000000') OR ([DifferentialBaseGuidState] IN ('Unknown','NotApplicable') AND [DifferentialBaseGuid] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_DifferentialBaseLsn", "([DifferentialBaseLsnState] = 'Known' AND [DifferentialBaseLsn] IS NOT NULL AND [DifferentialBaseLsn] >= 0) OR ([DifferentialBaseLsnState] IN ('Unknown','NotApplicable') AND [DifferentialBaseLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_DifferentialFields", "([TypeState] = 'Known' AND [Type] IS NOT NULL AND [Type] IN ('Full','Log') AND [DifferentialBaseLsnState] = 'NotApplicable' AND [DifferentialBaseGuidState] = 'NotApplicable') OR ([TypeState] = 'Known' AND [Type] IS NOT NULL AND [Type] = 'Differential' AND [DifferentialBaseLsnState] <> 'NotApplicable' AND [DifferentialBaseGuidState] <> 'NotApplicable') OR ([TypeState] = 'Unknown' AND [Type] IS NULL AND [DifferentialBaseLsnState] <> 'NotApplicable' AND [DifferentialBaseGuidState] <> 'NotApplicable')");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_EvidenceAtUtc", "[EvidenceAtUtc] IS NULL OR ([EvidenceAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[EvidenceAtUtc]) = 0)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_FamilyGuid", "([FamilyGuidState] = 'Known' AND [FamilyGuid] IS NOT NULL AND [FamilyGuid] <> '00000000-0000-0000-0000-000000000000') OR ([FamilyGuidState] IN ('Unknown') AND [FamilyGuid] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_FirstLsn", "([FirstLsnState] = 'Known' AND [FirstLsn] IS NOT NULL AND [FirstLsn] >= 0) OR ([FirstLsnState] IN ('Unknown') AND [FirstLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_FirstRecoveryForkId", "([FirstRecoveryForkIdState] = 'Known' AND [FirstRecoveryForkId] IS NOT NULL AND [FirstRecoveryForkId] <> '00000000-0000-0000-0000-000000000000') OR ([FirstRecoveryForkIdState] IN ('Unknown') AND [FirstRecoveryForkId] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_ForkPointLsn", "([ForkPointLsnState] = 'Known' AND [ForkPointLsn] IS NOT NULL AND [ForkPointLsn] >= 0) OR ([ForkPointLsnState] IN ('Unknown','NotApplicable') AND [ForkPointLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_HasBackupChecksums", "([HasBackupChecksumsState] = 'Known' AND [HasBackupChecksums] IS NOT NULL) OR ([HasBackupChecksumsState] IN ('Unknown') AND [HasBackupChecksums] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_HasIncompleteMetadata", "([HasIncompleteMetadataState] = 'Known' AND [HasIncompleteMetadata] IS NOT NULL) OR ([HasIncompleteMetadataState] IN ('Unknown') AND [HasIncompleteMetadata] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_Identity", "[Sequence] >= 1 AND [IntendedBackupSetId] <> '00000000-0000-0000-0000-000000000000' AND DATEPART(TZOFFSET,[ObservedAtUtc]) = 0");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_IsCompressed", "([IsCompressedState] = 'Known' AND [IsCompressed] IS NOT NULL) OR ([IsCompressedState] IN ('Unknown') AND [IsCompressed] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_IsCopyOnly", "([IsCopyOnlyState] = 'Known' AND [IsCopyOnly] IS NOT NULL) OR ([IsCopyOnlyState] IN ('Unknown') AND [IsCopyOnly] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_IsDamaged", "([IsDamagedState] = 'Known' AND [IsDamaged] IS NOT NULL) OR ([IsDamagedState] IN ('Unknown') AND [IsDamaged] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_IsSnapshot", "([IsSnapshotState] = 'Known' AND [IsSnapshot] IS NOT NULL) OR ([IsSnapshotState] IN ('Unknown') AND [IsSnapshot] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_LastLsn", "([LastLsnState] = 'Known' AND [LastLsn] IS NOT NULL AND [LastLsn] >= 0) OR ([LastLsnState] IN ('Unknown') AND [LastLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_Outcome", "[Outcome] IN ('Unknown','Succeeded','ConfirmedFailed','Indeterminate','Cancelled') AND [SqlOutcomeSource] IN ('Unknown','NotInvoked','PlatformResponse','RecoveredEvidence') AND ([SqlOutcomeSource] <> 'NotInvoked' OR [SqlSuccessObserved] = 0) AND [ObjectProtection] IN ('Unknown','Unsupported','GuardedUntilCommit')");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_PlatformCompletedAtUtc", "[PlatformCompletedAtUtc] IS NULL OR ([PlatformCompletedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[PlatformCompletedAtUtc]) = 0 AND [SqlOutcomeSource] = 'PlatformResponse' AND [SqlSuccessObserved] = 1)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_RecoveryForkId", "([RecoveryForkIdState] = 'Known' AND [RecoveryForkId] IS NOT NULL AND [RecoveryForkId] <> '00000000-0000-0000-0000-000000000000') OR ([RecoveryForkIdState] IN ('Unknown') AND [RecoveryForkId] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_SqlFinishedLocal", "([SqlFinishedLocalState] = 'Known' AND [SqlFinishedLocal] IS NOT NULL) OR ([SqlFinishedLocalState] IN ('Unknown') AND [SqlFinishedLocal] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_SqlStartedLocal", "([SqlStartedLocalState] = 'Known' AND [SqlStartedLocal] IS NOT NULL) OR ([SqlStartedLocalState] IN ('Unknown') AND [SqlStartedLocal] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_State", "[State] IN ('Reserved','Frozen','Applied','Abandoned') AND [Kind] IN ('Admission','SqlResult','Metadata','VerifyLocal','Transfer','ValidateCopy','Recovery') AND ([State] <> 'Abandoned' OR [Kind] NOT IN ('SqlResult','Transfer','ValidateCopy'))");
                    table.CheckConstraint("CK_BackupPlanExecutionOperations_Type", "([TypeState] = 'Known' AND [Type] IS NOT NULL AND [Type] IN ('Full','Differential','Log')) OR ([TypeState] IN ('Unknown') AND [Type] IS NULL)");
                    table.ForeignKey(
                        name: "FK_BackupPlanExecutionOperations_BackupAttempts_TaskId_AttemptId",
                        columns: x => new { x.TaskId, x.AttemptId },
                        principalTable: "BackupAttempts",
                        principalColumns: new[] { "TaskId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackupInvocationAuthorizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MutationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SqlOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TerminalObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TerminationKind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    TerminationEvidenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TerminationMutationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CallerIncarnationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SessionEstablishedLocal = table.Column<DateTime>(type: "datetime2(7)", nullable: true),
                    SessionId = table.Column<int>(type: "int", nullable: true),
                    BindingState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupInvocationAuthorizations", x => x.Id);
                    table.CheckConstraint("CK_BackupInvocationAuthorizations_Binding", "[CallerIncarnationId] <> '00000000-0000-0000-0000-000000000000' AND (([BindingState] = 'Unknown' AND [SessionId] IS NULL AND [SessionEstablishedLocal] IS NULL AND [ConnectionId] IS NULL) OR ([BindingState] = 'Known' AND [SessionId] IS NOT NULL AND [SessionId] > 0 AND [SessionEstablishedLocal] IS NOT NULL AND ([ConnectionId] IS NULL OR ([ConnectionId] IS NOT NULL AND [ConnectionId] <> '00000000-0000-0000-0000-000000000000'))))");
                    table.CheckConstraint("CK_BackupInvocationAuthorizations_GrantedAtUtc", "DATEPART(TZOFFSET,[GrantedAtUtc]) = 0");
                    table.CheckConstraint("CK_BackupInvocationAuthorizations_Termination", "([TerminalObservedAtUtc] IS NULL AND [TerminationKind] IS NULL AND [TerminationEvidenceId] IS NULL AND [TerminationMutationId] IS NULL) OR ([TerminalObservedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[TerminalObservedAtUtc]) = 0 AND [TerminationKind] IS NOT NULL AND [TerminationKind] IN ('PlatformCompleted','PlatformConfirmedFailed','RecoveredTerminated') AND ([TerminationKind] <> 'RecoveredTerminated' OR [BindingState] = 'Known') AND [TerminationEvidenceId] IS NOT NULL AND [TerminationEvidenceId] <> '00000000-0000-0000-0000-000000000000' AND [TerminationMutationId] IS NOT NULL AND [TerminationMutationId] <> '00000000-0000-0000-0000-000000000000')");
                    table.ForeignKey(
                        name: "FK_BackupInvocationAuthorizations_BackupAttempts_TaskId_AttemptId",
                        columns: x => new { x.TaskId, x.AttemptId },
                        principalTable: "BackupAttempts",
                        principalColumns: new[] { "TaskId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupInvocationAuthorizations_BackupPlanExecutionOperations_TaskId_AttemptId_SqlOperationId",
                        columns: x => new { x.TaskId, x.AttemptId, x.SqlOperationId },
                        principalTable: "BackupPlanExecutionOperations",
                        principalColumns: new[] { "TaskId", "AttemptId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupInvocationAuthorizations_BackupPlanExecutionOperations_TaskId_AttemptId_TerminationEvidenceId",
                        columns: x => new { x.TaskId, x.AttemptId, x.TerminationEvidenceId },
                        principalTable: "BackupPlanExecutionOperations",
                        principalColumns: new[] { "TaskId", "AttemptId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupInvocationAuthorizations_ManagedDatabases_DatabaseId",
                        column: x => x.DatabaseId,
                        principalTable: "ManagedDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackupPlanExecutionObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntryNumber = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ActualBaseBackupSetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EvidenceAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    OriginalCallTerminated = table.Column<bool>(type: "bit", nullable: false),
                    OriginalCallerCannotInvoke = table.Column<bool>(type: "bit", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PlatformCompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SqlOutcomeSource = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SqlSuccessObserved = table.Column<bool>(type: "bit", nullable: false),
                    UsedChecksum = table.Column<bool>(type: "bit", nullable: true),
                    UsedCompression = table.Column<bool>(type: "bit", nullable: true),
                    UsedCopyOnly = table.Column<bool>(type: "bit", nullable: true),
                    ActiveConclusion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ActiveReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ActiveAssessmentState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Conclusion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    BaselineReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AssessmentState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletionReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CompletionSource = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ContentDigestAlgorithm = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: true),
                    ContentDigest = table.Column<byte[]>(type: "binary(32)", nullable: true),
                    ContentLengthBytes = table.Column<long>(type: "bigint", nullable: true),
                    ObjectProtection = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ContentReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StableObjectId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ContentState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BackupSetGuidState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BackupSetGuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CheckpointLsnState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CheckpointLsn = table.Column<decimal>(type: "numeric(25,0)", nullable: true),
                    DatabaseBackupLsnState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DatabaseBackupLsn = table.Column<decimal>(type: "numeric(25,0)", nullable: true),
                    DatabaseGuidState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DatabaseGuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DifferentialBaseGuidState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DifferentialBaseGuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DifferentialBaseLsnState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DifferentialBaseLsn = table.Column<decimal>(type: "numeric(25,0)", nullable: true),
                    FamilyGuidState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FamilyGuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FirstLsnState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FirstLsn = table.Column<decimal>(type: "numeric(25,0)", nullable: true),
                    FirstRecoveryForkIdState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FirstRecoveryForkId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ForkPointLsnState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ForkPointLsn = table.Column<decimal>(type: "numeric(25,0)", nullable: true),
                    HasBackupChecksumsState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    HasBackupChecksums = table.Column<bool>(type: "bit", nullable: true),
                    HasIncompleteMetadataState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    HasIncompleteMetadata = table.Column<bool>(type: "bit", nullable: true),
                    IsCompressedState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsCompressed = table.Column<bool>(type: "bit", nullable: true),
                    IsCopyOnlyState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsCopyOnly = table.Column<bool>(type: "bit", nullable: true),
                    IsDamagedState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsDamaged = table.Column<bool>(type: "bit", nullable: true),
                    IsSnapshotState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsSnapshot = table.Column<bool>(type: "bit", nullable: true),
                    LastLsnState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LastLsn = table.Column<decimal>(type: "numeric(25,0)", nullable: true),
                    RecoveryForkIdState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RecoveryForkId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SqlFinishedLocalState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SqlFinishedLocal = table.Column<DateTime>(type: "datetime2(7)", nullable: true),
                    SqlStartedLocalState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SqlStartedLocal = table.Column<DateTime>(type: "datetime2(7)", nullable: true),
                    TypeState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupPlanExecutionObservations", x => x.Id);
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_ActiveAssessment", "([ActiveAssessmentState] = 'NotApplicable' AND [ActiveConclusion] IS NULL AND [ActiveReasonCode] IS NULL) OR ([ActiveAssessmentState] = 'Known' AND [ActiveConclusion] IS NOT NULL AND [ActiveConclusion] IN ('Verified','Unmanaged','Mismatch','Unknown') AND [ActiveReasonCode] IS NOT NULL AND [ActiveReasonCode] IN ('baseline.backup_type_mismatch','baseline.copy_only_differential','baseline.copy_only_full','baseline.database_backup_lsn_mismatch','baseline.database_identity_mismatch','baseline.duplicate_managed_guid','baseline.external_full_observed','baseline.history_not_found','baseline.lsn_mismatch','baseline.managed_full_not_found','baseline.managed_full_verified','baseline.missing_fields','baseline.multiple_bases','baseline.permission_denied','baseline.recovery_branch_mismatch','baseline.source_conflict'))");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_Assessment", "([AssessmentState] = 'NotApplicable' AND [Conclusion] IS NULL AND [BaselineReasonCode] IS NULL) OR ([AssessmentState] = 'Known' AND [Conclusion] IS NOT NULL AND [Conclusion] IN ('Verified','Unmanaged','Mismatch','Unknown') AND [BaselineReasonCode] IS NOT NULL AND [BaselineReasonCode] IN ('baseline.backup_type_mismatch','baseline.copy_only_differential','baseline.copy_only_full','baseline.database_backup_lsn_mismatch','baseline.database_identity_mismatch','baseline.duplicate_managed_guid','baseline.external_full_observed','baseline.history_not_found','baseline.lsn_mismatch','baseline.managed_full_not_found','baseline.managed_full_verified','baseline.missing_fields','baseline.multiple_bases','baseline.permission_denied','baseline.recovery_branch_mismatch','baseline.source_conflict'))");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_BackupSetGuid", "([BackupSetGuidState] = 'Known' AND [BackupSetGuid] IS NOT NULL AND [BackupSetGuid] <> '00000000-0000-0000-0000-000000000000') OR ([BackupSetGuidState] IN ('Unknown') AND [BackupSetGuid] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_CheckpointLsn", "([CheckpointLsnState] = 'Known' AND [CheckpointLsn] IS NOT NULL AND [CheckpointLsn] >= 0) OR ([CheckpointLsnState] IN ('Unknown') AND [CheckpointLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_Completion", "([CompletionSource] = 'PlatformObserved' AND [CompletedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[CompletedAtUtc]) = 0 AND [CompletionReasonCode] = 'completion.platform_observed') OR ([CompletionSource] = 'SqlLocalTime' AND [CompletedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[CompletedAtUtc]) = 0 AND [CompletionReasonCode] = 'completion.sql_local_converted') OR ([CompletionSource] = 'Unknown' AND [CompletedAtUtc] IS NULL AND [CompletionReasonCode] IN ('completion.ambiguous_local_time','completion.conversion_out_of_range','completion.future_sql_time','completion.invalid_local_time','completion.older_than_24_hours','completion.server_offset_mismatch','completion.server_offset_unknown','completion.server_time_zone_unknown','completion.sql_finish_missing'))");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_Content", "([ContentState] = 'Unknown' AND [ContentDigestAlgorithm] IS NULL AND [ContentDigest] IS NULL AND [ContentLengthBytes] IS NULL) OR ([ContentState] = 'Verified' AND [ContentDigestAlgorithm] IS NOT NULL AND [ContentDigestAlgorithm] = 'SHA256' AND [ContentDigest] IS NOT NULL AND [ContentLengthBytes] IS NOT NULL AND [ContentLengthBytes] > 0)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_DatabaseBackupLsn", "([DatabaseBackupLsnState] = 'Known' AND [DatabaseBackupLsn] IS NOT NULL AND [DatabaseBackupLsn] >= 0) OR ([DatabaseBackupLsnState] IN ('Unknown') AND [DatabaseBackupLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_DatabaseGuid", "([DatabaseGuidState] = 'Known' AND [DatabaseGuid] IS NOT NULL AND [DatabaseGuid] <> '00000000-0000-0000-0000-000000000000') OR ([DatabaseGuidState] IN ('Unknown') AND [DatabaseGuid] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_DifferentialBaseGuid", "([DifferentialBaseGuidState] = 'Known' AND [DifferentialBaseGuid] IS NOT NULL AND [DifferentialBaseGuid] <> '00000000-0000-0000-0000-000000000000') OR ([DifferentialBaseGuidState] IN ('Unknown','NotApplicable') AND [DifferentialBaseGuid] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_DifferentialBaseLsn", "([DifferentialBaseLsnState] = 'Known' AND [DifferentialBaseLsn] IS NOT NULL AND [DifferentialBaseLsn] >= 0) OR ([DifferentialBaseLsnState] IN ('Unknown','NotApplicable') AND [DifferentialBaseLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_DifferentialFields", "([TypeState] = 'Known' AND [Type] IS NOT NULL AND [Type] IN ('Full','Log') AND [DifferentialBaseLsnState] = 'NotApplicable' AND [DifferentialBaseGuidState] = 'NotApplicable') OR ([TypeState] = 'Known' AND [Type] IS NOT NULL AND [Type] = 'Differential' AND [DifferentialBaseLsnState] <> 'NotApplicable' AND [DifferentialBaseGuidState] <> 'NotApplicable') OR ([TypeState] = 'Unknown' AND [Type] IS NULL AND [DifferentialBaseLsnState] <> 'NotApplicable' AND [DifferentialBaseGuidState] <> 'NotApplicable')");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_EvidenceAtUtc", "[EvidenceAtUtc] IS NULL OR ([EvidenceAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[EvidenceAtUtc]) = 0)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_FamilyGuid", "([FamilyGuidState] = 'Known' AND [FamilyGuid] IS NOT NULL AND [FamilyGuid] <> '00000000-0000-0000-0000-000000000000') OR ([FamilyGuidState] IN ('Unknown') AND [FamilyGuid] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_FirstLsn", "([FirstLsnState] = 'Known' AND [FirstLsn] IS NOT NULL AND [FirstLsn] >= 0) OR ([FirstLsnState] IN ('Unknown') AND [FirstLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_FirstRecoveryForkId", "([FirstRecoveryForkIdState] = 'Known' AND [FirstRecoveryForkId] IS NOT NULL AND [FirstRecoveryForkId] <> '00000000-0000-0000-0000-000000000000') OR ([FirstRecoveryForkIdState] IN ('Unknown') AND [FirstRecoveryForkId] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_ForkPointLsn", "([ForkPointLsnState] = 'Known' AND [ForkPointLsn] IS NOT NULL AND [ForkPointLsn] >= 0) OR ([ForkPointLsnState] IN ('Unknown','NotApplicable') AND [ForkPointLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_HasBackupChecksums", "([HasBackupChecksumsState] = 'Known' AND [HasBackupChecksums] IS NOT NULL) OR ([HasBackupChecksumsState] IN ('Unknown') AND [HasBackupChecksums] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_HasIncompleteMetadata", "([HasIncompleteMetadataState] = 'Known' AND [HasIncompleteMetadata] IS NOT NULL) OR ([HasIncompleteMetadataState] IN ('Unknown') AND [HasIncompleteMetadata] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_IsCompressed", "([IsCompressedState] = 'Known' AND [IsCompressed] IS NOT NULL) OR ([IsCompressedState] IN ('Unknown') AND [IsCompressed] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_IsCopyOnly", "([IsCopyOnlyState] = 'Known' AND [IsCopyOnly] IS NOT NULL) OR ([IsCopyOnlyState] IN ('Unknown') AND [IsCopyOnly] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_IsDamaged", "([IsDamagedState] = 'Known' AND [IsDamaged] IS NOT NULL) OR ([IsDamagedState] IN ('Unknown') AND [IsDamaged] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_IsSnapshot", "([IsSnapshotState] = 'Known' AND [IsSnapshot] IS NOT NULL) OR ([IsSnapshotState] IN ('Unknown') AND [IsSnapshot] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_LastLsn", "([LastLsnState] = 'Known' AND [LastLsn] IS NOT NULL AND [LastLsn] >= 0) OR ([LastLsnState] IN ('Unknown') AND [LastLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_Outcome", "[Outcome] IN ('Unknown','Succeeded','ConfirmedFailed','Indeterminate','Cancelled') AND [SqlOutcomeSource] IN ('Unknown','NotInvoked','PlatformResponse','RecoveredEvidence') AND ([SqlOutcomeSource] <> 'NotInvoked' OR [SqlSuccessObserved] = 0) AND [ObjectProtection] IN ('Unknown','Unsupported','GuardedUntilCommit')");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_PlatformCompletedAtUtc", "[PlatformCompletedAtUtc] IS NULL OR ([PlatformCompletedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[PlatformCompletedAtUtc]) = 0 AND [SqlOutcomeSource] = 'PlatformResponse' AND [SqlSuccessObserved] = 1)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_RecoveryForkId", "([RecoveryForkIdState] = 'Known' AND [RecoveryForkId] IS NOT NULL AND [RecoveryForkId] <> '00000000-0000-0000-0000-000000000000') OR ([RecoveryForkIdState] IN ('Unknown') AND [RecoveryForkId] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_Source", "[EntryNumber] >= 0 AND [Source] IN ('Comparison','BackupHeader','Msdb','Database','Registration','SourceFile','RemotePartial','RemoteFinal','Termination') AND [Kind] IN ('Backup','Dependency','ActiveBaseline')");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_SqlFinishedLocal", "([SqlFinishedLocalState] = 'Known' AND [SqlFinishedLocal] IS NOT NULL) OR ([SqlFinishedLocalState] IN ('Unknown') AND [SqlFinishedLocal] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_SqlStartedLocal", "([SqlStartedLocalState] = 'Known' AND [SqlStartedLocal] IS NOT NULL) OR ([SqlStartedLocalState] IN ('Unknown') AND [SqlStartedLocal] IS NULL)");
                    table.CheckConstraint("CK_BackupPlanExecutionObservations_Type", "([TypeState] = 'Known' AND [Type] IS NOT NULL AND [Type] IN ('Full','Differential','Log')) OR ([TypeState] IN ('Unknown') AND [Type] IS NULL)");
                    table.ForeignKey(
                        name: "FK_BackupPlanExecutionObservations_BackupPlanExecutionOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "BackupPlanExecutionOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupAttempts_AdmittedFullBackupSetId",
                table: "BackupAttempts",
                column: "AdmittedFullBackupSetId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupAttempts_DifferentialAdmission",
                table: "BackupAttempts",
                sql: "([AdmittedFullBackupSetId] IS NULL AND [AdmissionRecoveryForkId] IS NULL AND [AdmissionObservedAtUtc] IS NULL) OR ([AdmittedFullBackupSetId] IS NOT NULL AND [AdmissionRecoveryForkId] IS NOT NULL AND [AdmissionObservedAtUtc] IS NOT NULL AND [ExpectedDatabaseGuid] IS NOT NULL AND [ExpectedFamilyGuid] IS NOT NULL AND [AdmittedFullBackupSetId] <> '00000000-0000-0000-0000-000000000000' AND [AdmissionRecoveryForkId] <> '00000000-0000-0000-0000-000000000000' AND DATEPART(TZOFFSET,[AdmissionObservedAtUtc]) = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupAttempts_SqlIdentity",
                table: "BackupAttempts",
                sql: "([ExpectedDatabaseGuid] IS NULL AND [ExpectedFamilyGuid] IS NULL) OR ([ExpectedDatabaseGuid] IS NOT NULL AND [ExpectedFamilyGuid] IS NOT NULL AND [ExpectedDatabaseGuid] <> '00000000-0000-0000-0000-000000000000' AND [ExpectedFamilyGuid] <> '00000000-0000-0000-0000-000000000000')");

            migrationBuilder.CreateIndex(
                name: "IX_BackupInvocationAuthorizations_DatabaseId",
                table: "BackupInvocationAuthorizations",
                column: "DatabaseId",
                unique: true,
                filter: "[TerminalObservedAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BackupInvocationAuthorizations_MutationId",
                table: "BackupInvocationAuthorizations",
                column: "MutationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupInvocationAuthorizations_TaskId_AttemptId",
                table: "BackupInvocationAuthorizations",
                columns: new[] { "TaskId", "AttemptId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupInvocationAuthorizations_TaskId_AttemptId_SqlOperationId",
                table: "BackupInvocationAuthorizations",
                columns: new[] { "TaskId", "AttemptId", "SqlOperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupInvocationAuthorizations_TaskId_AttemptId_TerminationEvidenceId",
                table: "BackupInvocationAuthorizations",
                columns: new[] { "TaskId", "AttemptId", "TerminationEvidenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupInvocationAuthorizations_TerminationMutationId",
                table: "BackupInvocationAuthorizations",
                column: "TerminationMutationId",
                unique: true,
                filter: "[TerminationMutationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BackupPlanExecutionObservations_OperationId_EntryNumber",
                table: "BackupPlanExecutionObservations",
                columns: new[] { "OperationId", "EntryNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupPlanExecutionOperations_TaskId_AttemptId_Kind_Sequence",
                table: "BackupPlanExecutionOperations",
                columns: new[] { "TaskId", "AttemptId", "Kind", "Sequence" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_BackupAttempts_BackupSets_AdmittedFullBackupSetId",
                table: "BackupAttempts",
                column: "AdmittedFullBackupSetId",
                principalTable: "BackupSets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [BackupInvocationAuthorizations])
                    OR EXISTS (SELECT 1 FROM [BackupPlanExecutionOperations])
                    OR EXISTS (SELECT 1 FROM [BackupPlanExecutionObservations])
                    OR EXISTS (SELECT 1 FROM [BackupAttempts] WHERE [ExpectedDatabaseGuid] IS NOT NULL
                        OR [ExpectedFamilyGuid] IS NOT NULL OR [AdmittedFullBackupSetId] IS NOT NULL
                        OR [AdmissionRecoveryForkId] IS NOT NULL OR [AdmissionObservedAtUtc] IS NOT NULL)
                BEGIN
                    THROW 51004, N'已有调用授权、执行回执或身份绑定，不能回退。请从迁移前的平台库备份恢复。', 1;
                END
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_BackupAttempts_BackupSets_AdmittedFullBackupSetId",
                table: "BackupAttempts");

            migrationBuilder.DropTable(
                name: "BackupInvocationAuthorizations");

            migrationBuilder.DropTable(
                name: "BackupPlanExecutionObservations");

            migrationBuilder.DropTable(
                name: "BackupPlanExecutionOperations");

            migrationBuilder.DropIndex(
                name: "IX_BackupAttempts_AdmittedFullBackupSetId",
                table: "BackupAttempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupAttempts_DifferentialAdmission",
                table: "BackupAttempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupAttempts_SqlIdentity",
                table: "BackupAttempts");

            migrationBuilder.DropColumn(
                name: "AdmissionObservedAtUtc",
                table: "BackupAttempts");

            migrationBuilder.DropColumn(
                name: "AdmissionRecoveryForkId",
                table: "BackupAttempts");

            migrationBuilder.DropColumn(
                name: "AdmittedFullBackupSetId",
                table: "BackupAttempts");

            migrationBuilder.DropColumn(
                name: "ExpectedDatabaseGuid",
                table: "BackupAttempts");

            migrationBuilder.DropColumn(
                name: "ExpectedFamilyGuid",
                table: "BackupAttempts");
        }
    }
}
