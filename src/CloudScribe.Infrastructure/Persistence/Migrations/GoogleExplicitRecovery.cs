using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class GoogleExplicitRecovery : Migration
{
    public const string MigrationId = "20261027090000_GoogleExplicitRecovery";
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE google_recovery_scopes (
            AccountId TEXT NOT NULL, ProjectId TEXT NOT NULL, Currency TEXT NOT NULL, Scale INTEGER NOT NULL,
            RetainedFloorMinorUnits INTEGER NOT NULL CHECK(RetainedFloorMinorUnits >= 0),
            ProjectCeilingMinorUnits INTEGER NOT NULL CHECK(ProjectCeilingMinorUnits >= RetainedFloorMinorUnits),
            AllocationRemainingMinorUnits INTEGER NOT NULL CHECK(AllocationRemainingMinorUnits >= 0),
            PRIMARY KEY(AccountId, ProjectId, Currency, Scale));
        CREATE TABLE google_recovery_attempts (
            AuthorizationId TEXT NOT NULL PRIMARY KEY, LogicalItemId TEXT NOT NULL,
            DocumentId TEXT NOT NULL, RevisionId TEXT NOT NULL, TextSha256 TEXT NOT NULL,
            RootHistoricalKey TEXT NOT NULL,RootHistoricalStateSha256 TEXT NOT NULL,AdmissionSha256 TEXT NOT NULL,
            AccountId TEXT NOT NULL, ProjectId TEXT NOT NULL, OperationId TEXT NOT NULL,
            PreviousKey TEXT NOT NULL, PreviousStateSha256 TEXT NOT NULL,
            ChildKey TEXT NOT NULL UNIQUE, ReviewSha256 TEXT NOT NULL UNIQUE, EnvelopeJson TEXT NOT NULL,
            ApprovedFinancialSha256 TEXT NOT NULL,
            PayloadSha256 TEXT NOT NULL, PayloadBytes INTEGER NOT NULL,
            Currency TEXT NOT NULL, Scale INTEGER NOT NULL, EstimateMinorUnits INTEGER NOT NULL,
            ReservedMinorUnits INTEGER NOT NULL CHECK(ReservedMinorUnits >= EstimateMinorUnits),
            BudgetEvidenceSha256 TEXT NOT NULL, ApprovedAtUnixMilliseconds INTEGER NOT NULL,
            Consumed INTEGER NOT NULL DEFAULT 0, ClaimedLocalAttemptId TEXT NULL,
            UNIQUE(AccountId, OperationId, PreviousKey));
        CREATE INDEX IX_google_recovery_logical ON google_recovery_attempts(LogicalItemId, ApprovedAtUnixMilliseconds);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Recovery history and retained reservations cannot be discarded by downgrade.");
}
