using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class ChirpArticlePlans : Migration
{
    public const string MigrationId = "20261029090000_ChirpArticlePlans";
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE chirp_article_plans (
            PlanSha256 TEXT NOT NULL PRIMARY KEY, DocumentId TEXT NOT NULL, RevisionId TEXT NOT NULL,
            AccountId TEXT NOT NULL, ProjectId TEXT NOT NULL, ProposalJson TEXT NOT NULL,
            LineageJson TEXT NOT NULL, ReusedAudioJson TEXT NOT NULL);
        CREATE TABLE chirp_article_attempts (
            AuthorizationId TEXT NOT NULL PRIMARY KEY, PlanSha256 TEXT NOT NULL, CoverageOrdinal INTEGER NOT NULL,
            PolicySha256 TEXT NOT NULL, SourceApprovalSha256 TEXT NOT NULL, AccountId TEXT NOT NULL, ProjectId TEXT NOT NULL,
            ChildKey TEXT NOT NULL UNIQUE, ReviewSha256 TEXT NOT NULL UNIQUE, EnvelopeJson TEXT NOT NULL,
            Currency TEXT NOT NULL, Scale INTEGER NOT NULL, EstimateMinorUnits INTEGER NOT NULL CHECK(EstimateMinorUnits>=0),
            ReservedMinorUnits INTEGER NOT NULL CHECK(ReservedMinorUnits>=EstimateMinorUnits),
            ApprovedFinancialSha256 TEXT NOT NULL, ApprovedAtUnixMilliseconds INTEGER NOT NULL,
            Consumed INTEGER NOT NULL DEFAULT 0 CHECK(Consumed IN(0,1)), ClaimedLocalAttemptId TEXT NULL,
            UNIQUE(PlanSha256,CoverageOrdinal), FOREIGN KEY(PlanSha256) REFERENCES chirp_article_plans(PlanSha256) ON DELETE RESTRICT);
        CREATE UNIQUE INDEX IX_chirp_article_local_attempt ON chirp_article_attempts(ClaimedLocalAttemptId) WHERE ClaimedLocalAttemptId IS NOT NULL;
        CREATE TABLE chirp_article_receipts (
            PlanSha256 TEXT NOT NULL, CoverageOrdinal INTEGER NOT NULL, AuthorizationId TEXT NOT NULL UNIQUE,
            ReceiptJson TEXT NOT NULL, ReceiptSha256 TEXT NOT NULL,
            PRIMARY KEY(PlanSha256,CoverageOrdinal),
            FOREIGN KEY(AuthorizationId) REFERENCES chirp_article_attempts(AuthorizationId) ON DELETE RESTRICT);
        CREATE TABLE chirp_article_output_journals (
            AuthorizationId TEXT NOT NULL PRIMARY KEY, ReceiptJson TEXT NOT NULL, ReceiptSha256 TEXT NOT NULL,
            FOREIGN KEY(AuthorizationId) REFERENCES chirp_article_attempts(AuthorizationId) ON DELETE RESTRICT);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Article-plan history, accepted receipts and retained reservations cannot be discarded by downgrade.");
}
