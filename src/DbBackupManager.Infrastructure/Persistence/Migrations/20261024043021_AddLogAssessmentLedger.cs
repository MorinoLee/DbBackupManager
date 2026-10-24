using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbBackupManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLogAssessmentLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LogAssessmentCommits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MutationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    PayloadFingerprint = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ScopeKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StorageTargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PreparedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CommittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TechnicalReadiness = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TechnicalReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DataStartAvailability = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DataStartReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CoverageConclusion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PriorCallMayEnterAdmission = table.Column<bool>(type: "bit", nullable: false),
                    PriorCallReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DatabaseBudgetAllows = table.Column<bool>(type: "bit", nullable: false),
                    DatabaseBudgetReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    GlobalBudgetAllows = table.Column<bool>(type: "bit", nullable: false),
                    GlobalBudgetReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogAssessmentCommits", x => x.Id);
                    table.UniqueConstraint("AK_LogAssessmentCommits_DatabaseId_MutationId", x => new { x.DatabaseId, x.MutationId });
                    table.CheckConstraint("CK_LogAssessmentCommits_CommittedAtUtc", "DATEPART(TZOFFSET,[CommittedAtUtc]) = 0");
                    table.CheckConstraint("CK_LogAssessmentCommits_ObservedAtUtc", "DATEPART(TZOFFSET,[ObservedAtUtc]) = 0");
                    table.CheckConstraint("CK_LogAssessmentCommits_PreparedAtUtc", "DATEPART(TZOFFSET,[PreparedAtUtc]) = 0");
                    table.CheckConstraint("CK_LogAssessmentCommits_SchemaVersion", "[SchemaVersion] >= 1");
                    table.CheckConstraint("CK_LogAssessmentCommits_Scope", "([ScopeKind] = 'Local' AND [StorageTargetId] IS NULL) OR ([ScopeKind] = 'Remote' AND [StorageTargetId] IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "LogGenerationFacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogGenerationFacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LogCoverageEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StorageTargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Conclusion = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    FrontierLsn = table.Column<decimal>(type: "decimal(25,0)", precision: 25, scale: 0, nullable: true),
                    RestorePointLsn = table.Column<decimal>(type: "decimal(25,0)", precision: 25, scale: 0, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogCoverageEntries", x => x.Id);
                    table.CheckConstraint("CK_LogCoverageEntries_Scope", "([ScopeKind] = 'Local' AND [StorageTargetId] IS NULL) OR ([ScopeKind] = 'Remote' AND [StorageTargetId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_LogCoverageEntries_LogAssessmentCommits_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "LogAssessmentCommits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LogObservationEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntryNumber = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    HasRequiredFields = table.Column<bool>(type: "bit", nullable: false),
                    FailureCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    FailurePhase = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogObservationEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LogObservationEntries_LogAssessmentCommits_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "LogAssessmentCommits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LogGenerationEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GenerationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvidenceKey = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    EvidenceKind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceDatabaseGuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FamilyGuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FirstRecoveryForkId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecoveryForkId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogGenerationEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LogGenerationEvidence_LogGenerationFacts_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "LogGenerationFacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LogCoverageEntries_AssessmentId",
                table: "LogCoverageEntries",
                column: "AssessmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogGenerationEvidence_EvidenceKey",
                table: "LogGenerationEvidence",
                column: "EvidenceKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogGenerationEvidence_GenerationId",
                table: "LogGenerationEvidence",
                column: "GenerationId");

            migrationBuilder.CreateIndex(
                name: "IX_LogObservationEntries_AssessmentId_EntryNumber",
                table: "LogObservationEntries",
                columns: new[] { "AssessmentId", "EntryNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [LogAssessmentCommits])
                OR EXISTS (SELECT 1 FROM [LogObservationEntries])
                OR EXISTS (SELECT 1 FROM [LogCoverageEntries])
                OR EXISTS (SELECT 1 FROM [LogGenerationFacts])
                OR EXISTS (SELECT 1 FROM [LogGenerationEvidence])
                OR EXISTS (SELECT 1 FROM [BackupSets])
                OR EXISTS (SELECT 1 FROM [BackupSetEvidence])
                OR EXISTS (SELECT 1 FROM [BackupTasks])
                OR EXISTS (SELECT 1 FROM [BackupAttempts])
                OR EXISTS (SELECT 1 FROM [BackupPlanExecutionOperations])
                OR EXISTS (SELECT 1 FROM [BackupPlanExecutionObservations])
                BEGIN
                    THROW 51015, N'已有 LOG 评估账本、备份、任务或执行数据，不能回退该迁移。请保留现场并从迁移前平台库备份恢复。', 1;
                END
                """);

            migrationBuilder.DropTable(
                name: "LogCoverageEntries");

            migrationBuilder.DropTable(
                name: "LogGenerationEvidence");

            migrationBuilder.DropTable(
                name: "LogObservationEntries");

            migrationBuilder.DropTable(
                name: "LogGenerationFacts");

            migrationBuilder.DropTable(
                name: "LogAssessmentCommits");
        }
    }
}
