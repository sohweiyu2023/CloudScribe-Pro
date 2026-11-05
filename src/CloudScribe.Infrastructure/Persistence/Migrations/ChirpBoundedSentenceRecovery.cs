using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class ChirpBoundedSentenceRecovery : Migration
{
    public const string MigrationId = "20261105110000_ChirpBoundedSentenceRecovery";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        CreateStorage(migrationBuilder);
        CreateOwnership(migrationBuilder);
        CreateImmutableGuards(migrationBuilder);
    }

    private static void CreateStorage(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE google_generation_sentence_rejections (
            AccountId TEXT NOT NULL, OperationStableId TEXT NOT NULL, IdempotencyKey TEXT NOT NULL,
            LocalAttemptId TEXT NOT NULL, PayloadSha256 TEXT NOT NULL, PayloadBytes INTEGER NOT NULL CHECK(PayloadBytes BETWEEN 1 AND 5000),
            ErrorStatus INTEGER NOT NULL, ValidationHint INTEGER NOT NULL, ObservedAtUnixMilliseconds INTEGER NOT NULL,
            PRIMARY KEY(AccountId,OperationStableId,IdempotencyKey,LocalAttemptId),
            FOREIGN KEY(AccountId,OperationStableId,IdempotencyKey) REFERENCES google_generation_queue_states(AccountId,OperationStableId,IdempotencyKey));
        CREATE TABLE chirp_article_split_attempts (
            AuthorizationId TEXT NOT NULL PRIMARY KEY, PlanSha256 TEXT NOT NULL, CoverageOrdinal INTEGER NOT NULL,
            PolicySha256 TEXT NOT NULL, SourceApprovalSha256 TEXT NOT NULL, AccountId TEXT NOT NULL, ProjectId TEXT NOT NULL,
            ChildKey TEXT NOT NULL UNIQUE, ReviewSha256 TEXT NOT NULL UNIQUE, EnvelopeJson TEXT NOT NULL,
            Currency TEXT NOT NULL, Scale INTEGER NOT NULL, EstimateMinorUnits INTEGER NOT NULL CHECK(EstimateMinorUnits>=0),
            ReservedMinorUnits INTEGER NOT NULL CHECK(ReservedMinorUnits>=EstimateMinorUnits),
            ApprovedFinancialSha256 TEXT NOT NULL, ApprovedAtUnixMilliseconds INTEGER NOT NULL,
            Consumed INTEGER NOT NULL DEFAULT 0 CHECK(Consumed IN(0,1)), ClaimedLocalAttemptId TEXT NULL,
            RootAuthorizationId TEXT NOT NULL, ParentAuthorizationId TEXT NOT NULL,
            ParentStateSha256 TEXT NOT NULL, GrantSha256 TEXT NOT NULL, GrantId TEXT NOT NULL,
            CHECK(AuthorizationId<>RootAuthorizationId AND AuthorizationId<>ParentAuthorizationId),
            FOREIGN KEY(RootAuthorizationId) REFERENCES chirp_article_authorizations(AuthorizationId),
            FOREIGN KEY(ParentAuthorizationId) REFERENCES chirp_article_authorizations(AuthorizationId),
            FOREIGN KEY(PlanSha256) REFERENCES chirp_article_plans(PlanSha256));
        CREATE UNIQUE INDEX IX_chirp_split_local_attempt ON chirp_article_split_attempts(ClaimedLocalAttemptId) WHERE ClaimedLocalAttemptId IS NOT NULL;
        CREATE TABLE chirp_article_split_mappings (
            AuthorizationId TEXT NOT NULL PRIMARY KEY, ParentAuthorizationId TEXT NOT NULL, ChildSlot INTEGER NOT NULL CHECK(ChildSlot IN(1,2)),
            JobId TEXT NOT NULL, MappingJson TEXT NOT NULL, MappingSha256 TEXT NOT NULL,
            UNIQUE(ParentAuthorizationId,ChildSlot), FOREIGN KEY(AuthorizationId) REFERENCES chirp_article_authorizations(AuthorizationId));
        CREATE TABLE chirp_article_split_receipts (
            AuthorizationId TEXT NOT NULL PRIMARY KEY, ReceiptJson TEXT NOT NULL, ReceiptSha256 TEXT NOT NULL,
            FOREIGN KEY(AuthorizationId) REFERENCES chirp_article_authorizations(AuthorizationId));
        """);

    private static void CreateOwnership(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP VIEW chirp_article_owned_attempts;
        CREATE VIEW chirp_article_owned_attempts AS SELECT * FROM chirp_article_attempts UNION ALL
            SELECT AuthorizationId,PlanSha256,CoverageOrdinal,PolicySha256,SourceApprovalSha256,AccountId,ProjectId,
                ChildKey,ReviewSha256,EnvelopeJson,Currency,Scale,EstimateMinorUnits,ReservedMinorUnits,
                ApprovedFinancialSha256,ApprovedAtUnixMilliseconds,Consumed,ClaimedLocalAttemptId FROM chirp_article_successor_attempts UNION ALL
            SELECT AuthorizationId,PlanSha256,CoverageOrdinal,PolicySha256,SourceApprovalSha256,AccountId,ProjectId,
                ChildKey,ReviewSha256,EnvelopeJson,Currency,Scale,EstimateMinorUnits,ReservedMinorUnits,
                ApprovedFinancialSha256,ApprovedAtUnixMilliseconds,Consumed,ClaimedLocalAttemptId FROM chirp_article_split_attempts;
        CREATE TRIGGER chirp_split_authorization AFTER INSERT ON chirp_article_split_attempts
        BEGIN INSERT INTO chirp_article_authorizations VALUES(NEW.AuthorizationId); END;
        CREATE TRIGGER chirp_split_binding_immutable BEFORE UPDATE ON chirp_article_split_attempts
        WHEN NEW.AuthorizationId IS NOT OLD.AuthorizationId OR NEW.PlanSha256 IS NOT OLD.PlanSha256 OR NEW.CoverageOrdinal IS NOT OLD.CoverageOrdinal
            OR NEW.PolicySha256 IS NOT OLD.PolicySha256 OR NEW.SourceApprovalSha256 IS NOT OLD.SourceApprovalSha256
            OR NEW.AccountId IS NOT OLD.AccountId OR NEW.ProjectId IS NOT OLD.ProjectId OR NEW.ChildKey IS NOT OLD.ChildKey
            OR NEW.ReviewSha256 IS NOT OLD.ReviewSha256 OR NEW.EnvelopeJson IS NOT OLD.EnvelopeJson OR NEW.Currency IS NOT OLD.Currency
            OR NEW.Scale IS NOT OLD.Scale OR NEW.EstimateMinorUnits IS NOT OLD.EstimateMinorUnits OR NEW.ReservedMinorUnits IS NOT OLD.ReservedMinorUnits
            OR NEW.ApprovedAtUnixMilliseconds IS NOT OLD.ApprovedAtUnixMilliseconds OR NEW.RootAuthorizationId IS NOT OLD.RootAuthorizationId
            OR NEW.ParentAuthorizationId IS NOT OLD.ParentAuthorizationId OR NEW.ParentStateSha256 IS NOT OLD.ParentStateSha256
            OR NEW.GrantSha256 IS NOT OLD.GrantSha256 OR NEW.GrantId IS NOT OLD.GrantId
        BEGIN SELECT RAISE(ABORT,'article-split-binding-is-immutable'); END;
        CREATE TRIGGER chirp_split_claim_monotonic BEFORE UPDATE ON chirp_article_split_attempts
        WHEN COALESCE((
            (NEW.ApprovedFinancialSha256 IS OLD.ApprovedFinancialSha256 AND NEW.Consumed IS OLD.Consumed AND NEW.ClaimedLocalAttemptId IS OLD.ClaimedLocalAttemptId)
            OR (OLD.ApprovedFinancialSha256='' AND length(NEW.ApprovedFinancialSha256)=64 AND NEW.ApprovedFinancialSha256 NOT GLOB '*[^0-9a-f]*'
                AND OLD.Consumed=0 AND NEW.Consumed=0 AND OLD.ClaimedLocalAttemptId IS NULL AND NEW.ClaimedLocalAttemptId IS NULL)
            OR (OLD.Consumed=0 AND NEW.Consumed=1 AND OLD.ClaimedLocalAttemptId IS NULL AND length(NEW.ClaimedLocalAttemptId)=32
                AND NEW.ClaimedLocalAttemptId NOT GLOB '*[^0-9a-f]*' AND length(OLD.ApprovedFinancialSha256)=64
                AND NEW.ApprovedFinancialSha256=OLD.ApprovedFinancialSha256)),0)=0
        BEGIN SELECT RAISE(ABORT,'article-split-claim-cannot-reset'); END;
        """);

    private static void CreateImmutableGuards(MigrationBuilder migrationBuilder)
    {
        foreach (string table in new[] { "google_generation_sentence_rejections", "chirp_article_split_mappings", "chirp_article_split_receipts" })
            migrationBuilder.Sql($"CREATE TRIGGER {table}_immutable BEFORE UPDATE ON {table} BEGIN SELECT RAISE(ABORT,'bounded-recovery-record-is-immutable'); END;");
        foreach (string table in new[] { "google_generation_sentence_rejections", "chirp_article_split_attempts", "chirp_article_split_mappings", "chirp_article_split_receipts" })
            migrationBuilder.Sql($"CREATE TRIGGER {table}_retained BEFORE DELETE ON {table} BEGIN SELECT RAISE(ABORT,'bounded-recovery-record-is-retained'); END;");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Bounded recovery reservations and actual accepted child evidence cannot be discarded.");
}
