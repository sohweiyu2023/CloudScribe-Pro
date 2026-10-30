using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class ChirpArticleSuccessors : Migration
{
    public const string MigrationId = "20261030050000_ChirpArticleSuccessors";
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        CreateSuccessorStorage(migrationBuilder);
        PreserveOutputOwnership(migrationBuilder);
        AddImmutableGuards(migrationBuilder);
    }

    private static void CreateSuccessorStorage(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE chirp_article_authorizations (AuthorizationId TEXT NOT NULL PRIMARY KEY);
        INSERT INTO chirp_article_authorizations SELECT AuthorizationId FROM chirp_article_attempts;
        CREATE TRIGGER chirp_root_authorization AFTER INSERT ON chirp_article_attempts
        BEGIN INSERT INTO chirp_article_authorizations VALUES(NEW.AuthorizationId); END;
        CREATE TABLE chirp_article_successor_attempts (
            AuthorizationId TEXT NOT NULL PRIMARY KEY, PlanSha256 TEXT NOT NULL, CoverageOrdinal INTEGER NOT NULL,
            PolicySha256 TEXT NOT NULL, SourceApprovalSha256 TEXT NOT NULL, AccountId TEXT NOT NULL, ProjectId TEXT NOT NULL,
            ChildKey TEXT NOT NULL UNIQUE, ReviewSha256 TEXT NOT NULL UNIQUE, EnvelopeJson TEXT NOT NULL,
            Currency TEXT NOT NULL, Scale INTEGER NOT NULL, EstimateMinorUnits INTEGER NOT NULL CHECK(EstimateMinorUnits>=0),
            ReservedMinorUnits INTEGER NOT NULL CHECK(ReservedMinorUnits>=EstimateMinorUnits),
            ApprovedFinancialSha256 TEXT NOT NULL, ApprovedAtUnixMilliseconds INTEGER NOT NULL,
            Consumed INTEGER NOT NULL DEFAULT 0 CHECK(Consumed IN(0,1)), ClaimedLocalAttemptId TEXT NULL,
            RootAuthorizationId TEXT NOT NULL, ParentAuthorizationId TEXT NOT NULL UNIQUE,
            ParentStateSha256 TEXT NOT NULL, GrantSha256 TEXT NOT NULL, GrantId TEXT NOT NULL UNIQUE,
            CHECK(AuthorizationId<>RootAuthorizationId AND AuthorizationId<>ParentAuthorizationId),
            FOREIGN KEY(RootAuthorizationId) REFERENCES chirp_article_attempts(AuthorizationId) ON DELETE RESTRICT,
            FOREIGN KEY(ParentAuthorizationId) REFERENCES chirp_article_authorizations(AuthorizationId) ON DELETE RESTRICT,
            FOREIGN KEY(PlanSha256) REFERENCES chirp_article_plans(PlanSha256) ON DELETE RESTRICT);
        CREATE UNIQUE INDEX IX_chirp_successor_local_attempt ON chirp_article_successor_attempts(ClaimedLocalAttemptId) WHERE ClaimedLocalAttemptId IS NOT NULL;
        CREATE UNIQUE INDEX IX_chirp_successor_source_scope ON chirp_article_successor_attempts(SourceApprovalSha256,RootAuthorizationId);
        CREATE TRIGGER chirp_successor_authorization AFTER INSERT ON chirp_article_successor_attempts
        BEGIN INSERT INTO chirp_article_authorizations VALUES(NEW.AuthorizationId); END;
        CREATE VIEW chirp_article_owned_attempts AS
            SELECT * FROM chirp_article_attempts UNION ALL
            SELECT AuthorizationId,PlanSha256,CoverageOrdinal,PolicySha256,SourceApprovalSha256,AccountId,ProjectId,
                ChildKey,ReviewSha256,EnvelopeJson,Currency,Scale,EstimateMinorUnits,ReservedMinorUnits,
                ApprovedFinancialSha256,ApprovedAtUnixMilliseconds,Consumed,ClaimedLocalAttemptId FROM chirp_article_successor_attempts;
        """);

    private static void PreserveOutputOwnership(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE chirp_article_receipts RENAME TO chirp_original_receipts;
        CREATE TABLE chirp_article_receipts (
            PlanSha256 TEXT NOT NULL, CoverageOrdinal INTEGER NOT NULL, AuthorizationId TEXT NOT NULL UNIQUE,
            ReceiptJson TEXT NOT NULL, ReceiptSha256 TEXT NOT NULL, PRIMARY KEY(PlanSha256,CoverageOrdinal),
            FOREIGN KEY(AuthorizationId) REFERENCES chirp_article_authorizations(AuthorizationId) ON DELETE RESTRICT);
        INSERT INTO chirp_article_receipts SELECT * FROM chirp_original_receipts;
        DROP TABLE chirp_original_receipts;
        ALTER TABLE chirp_article_output_journals RENAME TO chirp_original_journals;
        CREATE TABLE chirp_article_output_journals (
            AuthorizationId TEXT NOT NULL PRIMARY KEY, ReceiptJson TEXT NOT NULL, ReceiptSha256 TEXT NOT NULL,
            FOREIGN KEY(AuthorizationId) REFERENCES chirp_article_authorizations(AuthorizationId) ON DELETE RESTRICT);
        INSERT INTO chirp_article_output_journals SELECT * FROM chirp_original_journals;
        DROP TABLE chirp_original_journals;
        """);

    private static void AddImmutableGuards(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TRIGGER chirp_registry_requires_owner BEFORE INSERT ON chirp_article_authorizations
        WHEN (SELECT COUNT(*) FROM chirp_article_owned_attempts WHERE AuthorizationId=NEW.AuthorizationId)<>1
        BEGIN SELECT RAISE(ABORT,'article-authorization-requires-one-owner'); END;
        CREATE TRIGGER chirp_registry_immutable BEFORE UPDATE ON chirp_article_authorizations
        BEGIN SELECT RAISE(ABORT,'article-authorization-is-immutable'); END;
        CREATE TRIGGER chirp_registry_retained BEFORE DELETE ON chirp_article_authorizations
        BEGIN SELECT RAISE(ABORT,'article-authorization-is-retained'); END;
        CREATE TRIGGER chirp_root_retained BEFORE DELETE ON chirp_article_attempts
        BEGIN SELECT RAISE(ABORT,'article-original-attempt-is-retained'); END;
        CREATE TRIGGER chirp_successor_retained BEFORE DELETE ON chirp_article_successor_attempts
        BEGIN SELECT RAISE(ABORT,'article-successor-is-retained'); END;
        CREATE TRIGGER chirp_root_binding_immutable BEFORE UPDATE ON chirp_article_attempts
        WHEN NEW.AuthorizationId IS NOT OLD.AuthorizationId OR NEW.PlanSha256 IS NOT OLD.PlanSha256 OR NEW.CoverageOrdinal IS NOT OLD.CoverageOrdinal
            OR NEW.PolicySha256 IS NOT OLD.PolicySha256 OR NEW.SourceApprovalSha256 IS NOT OLD.SourceApprovalSha256
            OR NEW.AccountId IS NOT OLD.AccountId OR NEW.ProjectId IS NOT OLD.ProjectId OR NEW.ChildKey IS NOT OLD.ChildKey
            OR NEW.ReviewSha256 IS NOT OLD.ReviewSha256 OR NEW.EnvelopeJson IS NOT OLD.EnvelopeJson OR NEW.Currency IS NOT OLD.Currency
            OR NEW.Scale IS NOT OLD.Scale OR NEW.EstimateMinorUnits IS NOT OLD.EstimateMinorUnits OR NEW.ReservedMinorUnits IS NOT OLD.ReservedMinorUnits
            OR NEW.ApprovedAtUnixMilliseconds IS NOT OLD.ApprovedAtUnixMilliseconds
        BEGIN SELECT RAISE(ABORT,'article-original-binding-is-immutable'); END;
        CREATE TRIGGER chirp_successor_binding_immutable BEFORE UPDATE ON chirp_article_successor_attempts
        WHEN NEW.AuthorizationId IS NOT OLD.AuthorizationId OR NEW.PlanSha256 IS NOT OLD.PlanSha256 OR NEW.CoverageOrdinal IS NOT OLD.CoverageOrdinal
            OR NEW.PolicySha256 IS NOT OLD.PolicySha256 OR NEW.SourceApprovalSha256 IS NOT OLD.SourceApprovalSha256
            OR NEW.AccountId IS NOT OLD.AccountId OR NEW.ProjectId IS NOT OLD.ProjectId OR NEW.ChildKey IS NOT OLD.ChildKey
            OR NEW.ReviewSha256 IS NOT OLD.ReviewSha256 OR NEW.EnvelopeJson IS NOT OLD.EnvelopeJson OR NEW.Currency IS NOT OLD.Currency
            OR NEW.Scale IS NOT OLD.Scale OR NEW.EstimateMinorUnits IS NOT OLD.EstimateMinorUnits OR NEW.ReservedMinorUnits IS NOT OLD.ReservedMinorUnits
            OR NEW.ApprovedAtUnixMilliseconds IS NOT OLD.ApprovedAtUnixMilliseconds OR NEW.RootAuthorizationId IS NOT OLD.RootAuthorizationId
            OR NEW.ParentAuthorizationId IS NOT OLD.ParentAuthorizationId OR NEW.ParentStateSha256 IS NOT OLD.ParentStateSha256
            OR NEW.GrantSha256 IS NOT OLD.GrantSha256 OR NEW.GrantId IS NOT OLD.GrantId
        BEGIN SELECT RAISE(ABORT,'article-successor-binding-is-immutable'); END;
        CREATE TRIGGER chirp_root_claim_monotonic BEFORE UPDATE ON chirp_article_attempts
        WHEN COALESCE((
            (NEW.ApprovedFinancialSha256 IS OLD.ApprovedFinancialSha256 AND NEW.Consumed IS OLD.Consumed AND NEW.ClaimedLocalAttemptId IS OLD.ClaimedLocalAttemptId)
            OR (OLD.ApprovedFinancialSha256='' AND length(NEW.ApprovedFinancialSha256)=64 AND NEW.ApprovedFinancialSha256 NOT GLOB '*[^0-9a-f]*'
                AND OLD.Consumed=0 AND NEW.Consumed=0 AND OLD.ClaimedLocalAttemptId IS NULL AND NEW.ClaimedLocalAttemptId IS NULL)
            OR (OLD.Consumed=0 AND NEW.Consumed=1 AND OLD.ClaimedLocalAttemptId IS NULL AND length(NEW.ClaimedLocalAttemptId)=32
                AND NEW.ClaimedLocalAttemptId NOT GLOB '*[^0-9a-f]*' AND length(OLD.ApprovedFinancialSha256)=64 AND NEW.ApprovedFinancialSha256=OLD.ApprovedFinancialSha256)),0)=0
        BEGIN SELECT RAISE(ABORT,'article-original-claim-cannot-reset'); END;
        CREATE TRIGGER chirp_successor_claim_monotonic BEFORE UPDATE ON chirp_article_successor_attempts
        WHEN COALESCE((
            (NEW.ApprovedFinancialSha256 IS OLD.ApprovedFinancialSha256 AND NEW.Consumed IS OLD.Consumed AND NEW.ClaimedLocalAttemptId IS OLD.ClaimedLocalAttemptId)
            OR (OLD.ApprovedFinancialSha256='' AND length(NEW.ApprovedFinancialSha256)=64 AND NEW.ApprovedFinancialSha256 NOT GLOB '*[^0-9a-f]*'
                AND OLD.Consumed=0 AND NEW.Consumed=0 AND OLD.ClaimedLocalAttemptId IS NULL AND NEW.ClaimedLocalAttemptId IS NULL)
            OR (OLD.Consumed=0 AND NEW.Consumed=1 AND OLD.ClaimedLocalAttemptId IS NULL AND length(NEW.ClaimedLocalAttemptId)=32
                AND NEW.ClaimedLocalAttemptId NOT GLOB '*[^0-9a-f]*' AND length(OLD.ApprovedFinancialSha256)=64 AND NEW.ApprovedFinancialSha256=OLD.ApprovedFinancialSha256)),0)=0
        BEGIN SELECT RAISE(ABORT,'article-successor-claim-cannot-reset'); END;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Article successor history and retained financial holds cannot be discarded.");
}
