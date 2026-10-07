using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbBackupManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BackupFiles_TaskId_AttemptId",
                table: "BackupFiles");

            migrationBuilder.AddColumn<Guid>(
                name: "BackupSetId",
                table: "BackupFiles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BackupSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BaseBackupSetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SqlSuccessObserved = table.Column<bool>(type: "bit", nullable: false),
                    HasMetadataConflict = table.Column<bool>(type: "bit", nullable: false),
                    ReconciliationCount = table.Column<int>(type: "int", nullable: false),
                    BackupSetGuidIndex = table.Column<Guid>(type: "uniqueidentifier", nullable: true, computedColumnSql: "[BackupSetGuid]", stored: true),
                    Conclusion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    BaselineReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AssessmentState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletionReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CompletionSource = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
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
                    table.PrimaryKey("PK_BackupSets", x => x.Id);
                    table.UniqueConstraint("AK_BackupSets_DatabaseId_Id", x => new { x.DatabaseId, x.Id });
                    table.UniqueConstraint("AK_BackupSets_TaskId_AttemptId_DatabaseId_Id", x => new { x.TaskId, x.AttemptId, x.DatabaseId, x.Id });
                    table.UniqueConstraint("AK_BackupSets_TaskId_AttemptId_Id", x => new { x.TaskId, x.AttemptId, x.Id });
                    table.CheckConstraint("CK_BackupSets_Assessment", "([AssessmentState] = 'NotApplicable' AND [Conclusion] IS NULL AND [BaselineReasonCode] IS NULL) OR ([AssessmentState] = 'Known' AND [Conclusion] IS NOT NULL AND [Conclusion] IN ('Verified','Unmanaged','Mismatch','Unknown') AND [BaselineReasonCode] IS NOT NULL AND [BaselineReasonCode] IN ('baseline.backup_type_mismatch','baseline.copy_only_differential','baseline.copy_only_full','baseline.database_backup_lsn_mismatch','baseline.database_identity_mismatch','baseline.duplicate_managed_guid','baseline.external_full_observed','baseline.history_not_found','baseline.lsn_mismatch','baseline.managed_full_not_found','baseline.managed_full_verified','baseline.missing_fields','baseline.multiple_bases','baseline.permission_denied','baseline.recovery_branch_mismatch','baseline.source_conflict'))");
                    table.CheckConstraint("CK_BackupSets_BackupSetGuid", "([BackupSetGuidState] = 'Known' AND [BackupSetGuid] IS NOT NULL AND [BackupSetGuid] <> '00000000-0000-0000-0000-000000000000') OR ([BackupSetGuidState] IN ('Unknown') AND [BackupSetGuid] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_CheckpointLsn", "([CheckpointLsnState] = 'Known' AND [CheckpointLsn] IS NOT NULL AND [CheckpointLsn] >= 0) OR ([CheckpointLsnState] IN ('Unknown') AND [CheckpointLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_Completion", "([CompletionSource] = 'PlatformObserved' AND [CompletedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[CompletedAtUtc]) = 0 AND [CompletionReasonCode] = 'completion.platform_observed') OR ([CompletionSource] = 'SqlLocalTime' AND [CompletedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[CompletedAtUtc]) = 0 AND [CompletionReasonCode] = 'completion.sql_local_converted') OR ([CompletionSource] = 'Unknown' AND [CompletedAtUtc] IS NULL AND [CompletionReasonCode] IN ('completion.ambiguous_local_time','completion.conversion_out_of_range','completion.future_sql_time','completion.invalid_local_time','completion.older_than_24_hours','completion.server_offset_mismatch','completion.server_offset_unknown','completion.server_time_zone_unknown','completion.sql_finish_missing'))");
                    table.CheckConstraint("CK_BackupSets_Conflict", "[HasMetadataConflict] = 0 OR ([HasMetadataConflict] = 1 AND [Conclusion] IS NOT NULL AND [Conclusion] = 'Mismatch' AND [BaselineReasonCode] IS NOT NULL AND [BaselineReasonCode] = 'baseline.source_conflict')");
                    table.CheckConstraint("CK_BackupSets_DatabaseBackupLsn", "([DatabaseBackupLsnState] = 'Known' AND [DatabaseBackupLsn] IS NOT NULL AND [DatabaseBackupLsn] >= 0) OR ([DatabaseBackupLsnState] IN ('Unknown') AND [DatabaseBackupLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_DatabaseGuid", "([DatabaseGuidState] = 'Known' AND [DatabaseGuid] IS NOT NULL AND [DatabaseGuid] <> '00000000-0000-0000-0000-000000000000') OR ([DatabaseGuidState] IN ('Unknown') AND [DatabaseGuid] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_Dependency", "[BaseBackupSetId] IS NULL OR ([BaseBackupSetId] IS NOT NULL AND [BaseBackupSetId] <> [Id] AND [TypeState] = 'Known' AND [Type] IS NOT NULL AND [Type] = 'Differential')");
                    table.CheckConstraint("CK_BackupSets_DifferentialBaseGuid", "([DifferentialBaseGuidState] = 'Known' AND [DifferentialBaseGuid] IS NOT NULL AND [DifferentialBaseGuid] <> '00000000-0000-0000-0000-000000000000') OR ([DifferentialBaseGuidState] IN ('Unknown','NotApplicable') AND [DifferentialBaseGuid] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_DifferentialBaseLsn", "([DifferentialBaseLsnState] = 'Known' AND [DifferentialBaseLsn] IS NOT NULL AND [DifferentialBaseLsn] >= 0) OR ([DifferentialBaseLsnState] IN ('Unknown','NotApplicable') AND [DifferentialBaseLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_DifferentialFields", "([TypeState] = 'Known' AND [Type] IS NOT NULL AND [Type] IN ('Full','Log') AND [DifferentialBaseLsnState] = 'NotApplicable' AND [DifferentialBaseGuidState] = 'NotApplicable') OR ([TypeState] = 'Known' AND [Type] IS NOT NULL AND [Type] = 'Differential' AND [DifferentialBaseLsnState] <> 'NotApplicable' AND [DifferentialBaseGuidState] <> 'NotApplicable') OR ([TypeState] = 'Unknown' AND [Type] IS NULL AND [DifferentialBaseLsnState] <> 'NotApplicable' AND [DifferentialBaseGuidState] <> 'NotApplicable')");
                    table.CheckConstraint("CK_BackupSets_FamilyGuid", "([FamilyGuidState] = 'Known' AND [FamilyGuid] IS NOT NULL AND [FamilyGuid] <> '00000000-0000-0000-0000-000000000000') OR ([FamilyGuidState] IN ('Unknown') AND [FamilyGuid] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_FirstLsn", "([FirstLsnState] = 'Known' AND [FirstLsn] IS NOT NULL AND [FirstLsn] >= 0) OR ([FirstLsnState] IN ('Unknown') AND [FirstLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_FirstRecoveryForkId", "([FirstRecoveryForkIdState] = 'Known' AND [FirstRecoveryForkId] IS NOT NULL AND [FirstRecoveryForkId] <> '00000000-0000-0000-0000-000000000000') OR ([FirstRecoveryForkIdState] IN ('Unknown') AND [FirstRecoveryForkId] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_ForkPointLsn", "([ForkPointLsnState] = 'Known' AND [ForkPointLsn] IS NOT NULL AND [ForkPointLsn] >= 0) OR ([ForkPointLsnState] IN ('Unknown','NotApplicable') AND [ForkPointLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_HasBackupChecksums", "([HasBackupChecksumsState] = 'Known' AND [HasBackupChecksums] IS NOT NULL) OR ([HasBackupChecksumsState] IN ('Unknown') AND [HasBackupChecksums] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_HasIncompleteMetadata", "([HasIncompleteMetadataState] = 'Known' AND [HasIncompleteMetadata] IS NOT NULL) OR ([HasIncompleteMetadataState] IN ('Unknown') AND [HasIncompleteMetadata] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_IsCompressed", "([IsCompressedState] = 'Known' AND [IsCompressed] IS NOT NULL) OR ([IsCompressedState] IN ('Unknown') AND [IsCompressed] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_IsCopyOnly", "([IsCopyOnlyState] = 'Known' AND [IsCopyOnly] IS NOT NULL) OR ([IsCopyOnlyState] IN ('Unknown') AND [IsCopyOnly] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_IsDamaged", "([IsDamagedState] = 'Known' AND [IsDamaged] IS NOT NULL) OR ([IsDamagedState] IN ('Unknown') AND [IsDamaged] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_IsSnapshot", "([IsSnapshotState] = 'Known' AND [IsSnapshot] IS NOT NULL) OR ([IsSnapshotState] IN ('Unknown') AND [IsSnapshot] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_LastLsn", "([LastLsnState] = 'Known' AND [LastLsn] IS NOT NULL AND [LastLsn] >= 0) OR ([LastLsnState] IN ('Unknown') AND [LastLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_ReconciliationCount", "[ReconciliationCount] >= 1");
                    table.CheckConstraint("CK_BackupSets_RecoveryForkId", "([RecoveryForkIdState] = 'Known' AND [RecoveryForkId] IS NOT NULL AND [RecoveryForkId] <> '00000000-0000-0000-0000-000000000000') OR ([RecoveryForkIdState] IN ('Unknown') AND [RecoveryForkId] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_SqlFinishedLocal", "([SqlFinishedLocalState] = 'Known' AND [SqlFinishedLocal] IS NOT NULL) OR ([SqlFinishedLocalState] IN ('Unknown') AND [SqlFinishedLocal] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_SqlStartedLocal", "([SqlStartedLocalState] = 'Known' AND [SqlStartedLocal] IS NOT NULL) OR ([SqlStartedLocalState] IN ('Unknown') AND [SqlStartedLocal] IS NULL)");
                    table.CheckConstraint("CK_BackupSets_Type", "([TypeState] = 'Known' AND [Type] IS NOT NULL AND [Type] IN ('Full','Differential','Log')) OR ([TypeState] IN ('Unknown') AND [Type] IS NULL)");
                    table.ForeignKey(
                        name: "FK_BackupSets_BackupAttempts_TaskId_AttemptId",
                        columns: x => new { x.TaskId, x.AttemptId },
                        principalTable: "BackupAttempts",
                        principalColumns: new[] { "TaskId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupSets_BackupSets_DatabaseId_BaseBackupSetId",
                        columns: x => new { x.DatabaseId, x.BaseBackupSetId },
                        principalTable: "BackupSets",
                        principalColumns: new[] { "DatabaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupSets_ManagedDatabases_DatabaseId",
                        column: x => x.DatabaseId,
                        principalTable: "ManagedDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackupSetEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BackupSetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestedBaseBackupSetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MutationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReconciliationNumber = table.Column<int>(type: "int", nullable: false),
                    EntryNumber = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SqlSuccessObserved = table.Column<bool>(type: "bit", nullable: false),
                    Conclusion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    BaselineReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AssessmentState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletionReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CompletionSource = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
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
                    table.PrimaryKey("PK_BackupSetEvidence", x => x.Id);
                    table.CheckConstraint("CK_BackupSetEvidence_Assessment", "([AssessmentState] = 'NotApplicable' AND [Conclusion] IS NULL AND [BaselineReasonCode] IS NULL) OR ([AssessmentState] = 'Known' AND [Conclusion] IS NOT NULL AND [Conclusion] IN ('Verified','Unmanaged','Mismatch','Unknown') AND [BaselineReasonCode] IS NOT NULL AND [BaselineReasonCode] IN ('baseline.backup_type_mismatch','baseline.copy_only_differential','baseline.copy_only_full','baseline.database_backup_lsn_mismatch','baseline.database_identity_mismatch','baseline.duplicate_managed_guid','baseline.external_full_observed','baseline.history_not_found','baseline.lsn_mismatch','baseline.managed_full_not_found','baseline.managed_full_verified','baseline.missing_fields','baseline.multiple_bases','baseline.permission_denied','baseline.recovery_branch_mismatch','baseline.source_conflict'))");
                    table.CheckConstraint("CK_BackupSetEvidence_BackupSetGuid", "([BackupSetGuidState] = 'Known' AND [BackupSetGuid] IS NOT NULL AND [BackupSetGuid] <> '00000000-0000-0000-0000-000000000000') OR ([BackupSetGuidState] IN ('Unknown') AND [BackupSetGuid] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_CheckpointLsn", "([CheckpointLsnState] = 'Known' AND [CheckpointLsn] IS NOT NULL AND [CheckpointLsn] >= 0) OR ([CheckpointLsnState] IN ('Unknown') AND [CheckpointLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_Completion", "([CompletionSource] = 'PlatformObserved' AND [CompletedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[CompletedAtUtc]) = 0 AND [CompletionReasonCode] = 'completion.platform_observed') OR ([CompletionSource] = 'SqlLocalTime' AND [CompletedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[CompletedAtUtc]) = 0 AND [CompletionReasonCode] = 'completion.sql_local_converted') OR ([CompletionSource] = 'Unknown' AND [CompletedAtUtc] IS NULL AND [CompletionReasonCode] IN ('completion.ambiguous_local_time','completion.conversion_out_of_range','completion.future_sql_time','completion.invalid_local_time','completion.older_than_24_hours','completion.server_offset_mismatch','completion.server_offset_unknown','completion.server_time_zone_unknown','completion.sql_finish_missing'))");
                    table.CheckConstraint("CK_BackupSetEvidence_DatabaseBackupLsn", "([DatabaseBackupLsnState] = 'Known' AND [DatabaseBackupLsn] IS NOT NULL AND [DatabaseBackupLsn] >= 0) OR ([DatabaseBackupLsnState] IN ('Unknown') AND [DatabaseBackupLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_DatabaseGuid", "([DatabaseGuidState] = 'Known' AND [DatabaseGuid] IS NOT NULL AND [DatabaseGuid] <> '00000000-0000-0000-0000-000000000000') OR ([DatabaseGuidState] IN ('Unknown') AND [DatabaseGuid] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_DifferentialBaseGuid", "([DifferentialBaseGuidState] = 'Known' AND [DifferentialBaseGuid] IS NOT NULL AND [DifferentialBaseGuid] <> '00000000-0000-0000-0000-000000000000') OR ([DifferentialBaseGuidState] IN ('Unknown','NotApplicable') AND [DifferentialBaseGuid] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_DifferentialBaseLsn", "([DifferentialBaseLsnState] = 'Known' AND [DifferentialBaseLsn] IS NOT NULL AND [DifferentialBaseLsn] >= 0) OR ([DifferentialBaseLsnState] IN ('Unknown','NotApplicable') AND [DifferentialBaseLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_DifferentialFields", "([TypeState] = 'Known' AND [Type] IS NOT NULL AND [Type] IN ('Full','Log') AND [DifferentialBaseLsnState] = 'NotApplicable' AND [DifferentialBaseGuidState] = 'NotApplicable') OR ([TypeState] = 'Known' AND [Type] IS NOT NULL AND [Type] = 'Differential' AND [DifferentialBaseLsnState] <> 'NotApplicable' AND [DifferentialBaseGuidState] <> 'NotApplicable') OR ([TypeState] = 'Unknown' AND [Type] IS NULL AND [DifferentialBaseLsnState] <> 'NotApplicable' AND [DifferentialBaseGuidState] <> 'NotApplicable')");
                    table.CheckConstraint("CK_BackupSetEvidence_FamilyGuid", "([FamilyGuidState] = 'Known' AND [FamilyGuid] IS NOT NULL AND [FamilyGuid] <> '00000000-0000-0000-0000-000000000000') OR ([FamilyGuidState] IN ('Unknown') AND [FamilyGuid] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_FirstLsn", "([FirstLsnState] = 'Known' AND [FirstLsn] IS NOT NULL AND [FirstLsn] >= 0) OR ([FirstLsnState] IN ('Unknown') AND [FirstLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_FirstRecoveryForkId", "([FirstRecoveryForkIdState] = 'Known' AND [FirstRecoveryForkId] IS NOT NULL AND [FirstRecoveryForkId] <> '00000000-0000-0000-0000-000000000000') OR ([FirstRecoveryForkIdState] IN ('Unknown') AND [FirstRecoveryForkId] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_ForkPointLsn", "([ForkPointLsnState] = 'Known' AND [ForkPointLsn] IS NOT NULL AND [ForkPointLsn] >= 0) OR ([ForkPointLsnState] IN ('Unknown','NotApplicable') AND [ForkPointLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_HasBackupChecksums", "([HasBackupChecksumsState] = 'Known' AND [HasBackupChecksums] IS NOT NULL) OR ([HasBackupChecksumsState] IN ('Unknown') AND [HasBackupChecksums] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_HasIncompleteMetadata", "([HasIncompleteMetadataState] = 'Known' AND [HasIncompleteMetadata] IS NOT NULL) OR ([HasIncompleteMetadataState] IN ('Unknown') AND [HasIncompleteMetadata] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_IsCompressed", "([IsCompressedState] = 'Known' AND [IsCompressed] IS NOT NULL) OR ([IsCompressedState] IN ('Unknown') AND [IsCompressed] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_IsCopyOnly", "([IsCopyOnlyState] = 'Known' AND [IsCopyOnly] IS NOT NULL) OR ([IsCopyOnlyState] IN ('Unknown') AND [IsCopyOnly] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_IsDamaged", "([IsDamagedState] = 'Known' AND [IsDamaged] IS NOT NULL) OR ([IsDamagedState] IN ('Unknown') AND [IsDamaged] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_IsSnapshot", "([IsSnapshotState] = 'Known' AND [IsSnapshot] IS NOT NULL) OR ([IsSnapshotState] IN ('Unknown') AND [IsSnapshot] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_Kind", "[Kind] IN ('Backup','Dependency','ActiveBaseline')");
                    table.CheckConstraint("CK_BackupSetEvidence_LastLsn", "([LastLsnState] = 'Known' AND [LastLsn] IS NOT NULL AND [LastLsn] >= 0) OR ([LastLsnState] IN ('Unknown') AND [LastLsn] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_ObservedAtUtc", "DATEPART(TZOFFSET,[ObservedAtUtc]) = 0");
                    table.CheckConstraint("CK_BackupSetEvidence_RecoveryForkId", "([RecoveryForkIdState] = 'Known' AND [RecoveryForkId] IS NOT NULL AND [RecoveryForkId] <> '00000000-0000-0000-0000-000000000000') OR ([RecoveryForkIdState] IN ('Unknown') AND [RecoveryForkId] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_Sequence", "[ReconciliationNumber] >= 1 AND [EntryNumber] >= 0");
                    table.CheckConstraint("CK_BackupSetEvidence_Source", "[Source] IN ('Comparison','BackupHeader','Msdb','Database','Registration')");
                    table.CheckConstraint("CK_BackupSetEvidence_SqlFinishedLocal", "([SqlFinishedLocalState] = 'Known' AND [SqlFinishedLocal] IS NOT NULL) OR ([SqlFinishedLocalState] IN ('Unknown') AND [SqlFinishedLocal] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_SqlStartedLocal", "([SqlStartedLocalState] = 'Known' AND [SqlStartedLocal] IS NOT NULL) OR ([SqlStartedLocalState] IN ('Unknown') AND [SqlStartedLocal] IS NULL)");
                    table.CheckConstraint("CK_BackupSetEvidence_Type", "([TypeState] = 'Known' AND [Type] IS NOT NULL AND [Type] IN ('Full','Differential','Log')) OR ([TypeState] IN ('Unknown') AND [Type] IS NULL)");
                    table.ForeignKey(
                        name: "FK_BackupSetEvidence_BackupAttempts_TaskId_AttemptId",
                        columns: x => new { x.TaskId, x.AttemptId },
                        principalTable: "BackupAttempts",
                        principalColumns: new[] { "TaskId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupSetEvidence_BackupSets_RequestedBaseBackupSetId",
                        column: x => x.RequestedBaseBackupSetId,
                        principalTable: "BackupSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupSetEvidence_BackupSets_TaskId_AttemptId_BackupSetId",
                        columns: x => new { x.TaskId, x.AttemptId, x.BackupSetId },
                        principalTable: "BackupSets",
                        principalColumns: new[] { "TaskId", "AttemptId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupFiles_TaskId_AttemptId_DatabaseId_BackupSetId",
                table: "BackupFiles",
                columns: new[] { "TaskId", "AttemptId", "DatabaseId", "BackupSetId" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupSetEvidence_AttemptId_ReconciliationNumber_EntryNumber",
                table: "BackupSetEvidence",
                columns: new[] { "AttemptId", "ReconciliationNumber", "EntryNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupSetEvidence_RequestedBaseBackupSetId",
                table: "BackupSetEvidence",
                column: "RequestedBaseBackupSetId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupSetEvidence_TaskId_AttemptId_BackupSetId",
                table: "BackupSetEvidence",
                columns: new[] { "TaskId", "AttemptId", "BackupSetId" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupSetEvidence_TaskId_AttemptId_MutationId_EntryNumber",
                table: "BackupSetEvidence",
                columns: new[] { "TaskId", "AttemptId", "MutationId", "EntryNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupSets_AttemptId",
                table: "BackupSets",
                column: "AttemptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupSets_BackupSetGuidIndex",
                table: "BackupSets",
                column: "BackupSetGuidIndex",
                unique: true,
                filter: "[BackupSetGuid] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BackupSets_DatabaseId_BaseBackupSetId",
                table: "BackupSets",
                columns: new[] { "DatabaseId", "BaseBackupSetId" });

            migrationBuilder.AddForeignKey(
                name: "FK_BackupFiles_BackupSets_TaskId_AttemptId_DatabaseId_BackupSetId",
                table: "BackupFiles",
                columns: new[] { "TaskId", "AttemptId", "DatabaseId", "BackupSetId" },
                principalTable: "BackupSets",
                principalColumns: new[] { "TaskId", "AttemptId", "DatabaseId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BackupSetMigrationGuard.RejectDownWhenDataExists);

            migrationBuilder.DropForeignKey(
                name: "FK_BackupFiles_BackupSets_TaskId_AttemptId_DatabaseId_BackupSetId",
                table: "BackupFiles");

            migrationBuilder.DropTable(
                name: "BackupSetEvidence");

            migrationBuilder.DropTable(
                name: "BackupSets");

            migrationBuilder.DropIndex(
                name: "IX_BackupFiles_TaskId_AttemptId_DatabaseId_BackupSetId",
                table: "BackupFiles");

            migrationBuilder.DropColumn(
                name: "BackupSetId",
                table: "BackupFiles");

            migrationBuilder.CreateIndex(
                name: "IX_BackupFiles_TaskId_AttemptId",
                table: "BackupFiles",
                columns: new[] { "TaskId", "AttemptId" });
        }
    }
}
