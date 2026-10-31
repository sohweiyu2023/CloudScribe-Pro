using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class ChirpReservationCatalogMappings : Migration
{
    public const string MigrationId = "20261031063000_ChirpReservationCatalogMappings";
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE chirp_reserved_catalog_mappings (
            AuthorizationId TEXT NOT NULL, MappingId TEXT NOT NULL PRIMARY KEY,
            PolicyJson TEXT NOT NULL, PolicySha256 TEXT NOT NULL UNIQUE, CreatedAtUnixMilliseconds INTEGER NOT NULL, PriorMappingSha256 TEXT NULL,
            FOREIGN KEY(AuthorizationId) REFERENCES chirp_article_authorizations(AuthorizationId) ON DELETE RESTRICT);
        CREATE UNIQUE INDEX IX_chirp_reserved_one_root ON chirp_reserved_catalog_mappings(AuthorizationId) WHERE PriorMappingSha256 IS NULL;
        CREATE UNIQUE INDEX IX_chirp_reserved_one_child ON chirp_reserved_catalog_mappings(AuthorizationId,PriorMappingSha256) WHERE PriorMappingSha256 IS NOT NULL;
        CREATE TABLE chirp_reserved_dispatch_mappings (
            AuthorizationId TEXT NOT NULL PRIMARY KEY, MappingId TEXT NOT NULL UNIQUE,
            PolicySha256 TEXT NOT NULL, ClaimedLocalAttemptId TEXT NOT NULL UNIQUE, ClaimedAtUnixMilliseconds INTEGER NOT NULL,
            FOREIGN KEY(AuthorizationId) REFERENCES chirp_article_authorizations(AuthorizationId) ON DELETE RESTRICT,
            FOREIGN KEY(MappingId) REFERENCES chirp_reserved_catalog_mappings(MappingId) ON DELETE RESTRICT);
        CREATE TRIGGER chirp_reserved_mapping_requires_parent BEFORE INSERT ON chirp_reserved_catalog_mappings
        WHEN NEW.PriorMappingSha256 IS NOT NULL AND NOT EXISTS(SELECT 1 FROM chirp_reserved_catalog_mappings
            WHERE AuthorizationId=NEW.AuthorizationId AND PolicySha256=NEW.PriorMappingSha256)
        BEGIN SELECT RAISE(ABORT,'reserved-catalog-mapping-parent-is-missing'); END;
        CREATE TRIGGER chirp_reserved_mapping_requires_clean_owner BEFORE INSERT ON chirp_reserved_catalog_mappings
        WHEN NOT EXISTS(SELECT 1 FROM chirp_article_owned_attempts a JOIN google_generation_queue_states q
            ON q.AccountId=a.AccountId AND q.OperationStableId='synthesize-speech' AND q.IdempotencyKey=a.ChildKey
            WHERE a.AuthorizationId=NEW.AuthorizationId AND a.Consumed=0 AND a.ClaimedLocalAttemptId IS NULL
            AND q.SubmissionAttempted=0 AND q.UnresolvedSubmission=0 AND q.LocalAttemptId IS NULL AND q.ProviderRequestId IS NULL)
        BEGIN SELECT RAISE(ABORT,'reserved-catalog-mapping-requires-unsubmitted-owner'); END;
        CREATE TRIGGER chirp_reserved_mapping_immutable BEFORE UPDATE ON chirp_reserved_catalog_mappings
        BEGIN SELECT RAISE(ABORT,'reserved-catalog-mapping-is-immutable'); END;
        CREATE TRIGGER chirp_reserved_mapping_retained BEFORE DELETE ON chirp_reserved_catalog_mappings
        BEGIN SELECT RAISE(ABORT,'reserved-catalog-mapping-is-retained'); END;
        CREATE TRIGGER chirp_reserved_dispatch_immutable BEFORE UPDATE ON chirp_reserved_dispatch_mappings
        BEGIN SELECT RAISE(ABORT,'reserved-dispatch-mapping-is-immutable'); END;
        CREATE TRIGGER chirp_reserved_dispatch_retained BEFORE DELETE ON chirp_reserved_dispatch_mappings
        BEGIN SELECT RAISE(ABORT,'reserved-dispatch-mapping-is-retained'); END;
        CREATE TRIGGER chirp_reserved_dispatch_requires_clean_head BEFORE INSERT ON chirp_reserved_dispatch_mappings
        WHEN NOT EXISTS(SELECT 1 FROM chirp_article_owned_attempts a JOIN chirp_reserved_catalog_mappings m ON m.AuthorizationId=a.AuthorizationId
            JOIN google_generation_queue_states q ON q.AccountId=a.AccountId AND q.OperationStableId='synthesize-speech' AND q.IdempotencyKey=a.ChildKey
            WHERE a.AuthorizationId=NEW.AuthorizationId AND a.Consumed=0 AND a.ClaimedLocalAttemptId IS NULL
            AND q.SubmissionAttempted=0 AND q.UnresolvedSubmission=0 AND q.LocalAttemptId IS NULL AND q.ProviderRequestId IS NULL
            AND m.MappingId=NEW.MappingId AND m.PolicySha256=NEW.PolicySha256
            AND NOT EXISTS(SELECT 1 FROM chirp_reserved_catalog_mappings child WHERE child.AuthorizationId=a.AuthorizationId AND child.PriorMappingSha256=m.PolicySha256))
        BEGIN SELECT RAISE(ABORT,'reserved-dispatch-requires-clean-exact-mapping-head'); END;
        CREATE TRIGGER chirp_reserved_root_claim_requires_binding BEFORE UPDATE ON chirp_article_attempts
        WHEN OLD.Consumed=0 AND NEW.Consumed=1 AND EXISTS(SELECT 1 FROM chirp_reserved_catalog_mappings WHERE AuthorizationId=OLD.AuthorizationId)
            AND NOT EXISTS(SELECT 1 FROM chirp_reserved_dispatch_mappings WHERE AuthorizationId=OLD.AuthorizationId AND ClaimedLocalAttemptId=NEW.ClaimedLocalAttemptId)
        BEGIN SELECT RAISE(ABORT,'mapped-reservation-requires-atomic-dispatch-binding'); END;
        CREATE TRIGGER chirp_reserved_successor_claim_requires_binding BEFORE UPDATE ON chirp_article_successor_attempts
        WHEN OLD.Consumed=0 AND NEW.Consumed=1 AND EXISTS(SELECT 1 FROM chirp_reserved_catalog_mappings WHERE AuthorizationId=OLD.AuthorizationId)
            AND NOT EXISTS(SELECT 1 FROM chirp_reserved_dispatch_mappings WHERE AuthorizationId=OLD.AuthorizationId AND ClaimedLocalAttemptId=NEW.ClaimedLocalAttemptId)
        BEGIN SELECT RAISE(ABORT,'mapped-reservation-requires-atomic-dispatch-binding'); END;
        CREATE TRIGGER chirp_reserved_queue_claim_requires_binding BEFORE UPDATE ON google_generation_queue_states
        WHEN OLD.SubmissionAttempted=0 AND NEW.SubmissionAttempted=1
            AND EXISTS(SELECT 1 FROM chirp_article_owned_attempts a JOIN chirp_reserved_catalog_mappings m ON m.AuthorizationId=a.AuthorizationId
                WHERE a.AccountId=OLD.AccountId AND a.ChildKey=OLD.IdempotencyKey AND OLD.OperationStableId='synthesize-speech')
            AND NOT EXISTS(SELECT 1 FROM chirp_article_owned_attempts a JOIN chirp_reserved_dispatch_mappings d ON d.AuthorizationId=a.AuthorizationId
                JOIN chirp_reserved_catalog_mappings m ON m.MappingId=d.MappingId AND m.AuthorizationId=d.AuthorizationId AND m.PolicySha256=d.PolicySha256
                WHERE a.AccountId=OLD.AccountId AND a.ChildKey=OLD.IdempotencyKey AND a.Consumed=1 AND a.ClaimedLocalAttemptId=NEW.LocalAttemptId
                AND d.ClaimedLocalAttemptId=NEW.LocalAttemptId AND NEW.UnresolvedSubmission=1 AND NEW.ProviderRequestId IS NULL
                AND NEW.DiagnosticCode='google-dispatch-claimed'
                AND NEW.PayloadSha256=json_extract(m.PolicyJson,'$.ExecutionEnvelope.CompiledPayloadSha256')
                AND NEW.PayloadBytes=json_extract(m.PolicyJson,'$.ExecutionEnvelope.CompiledPayloadBytes'))
        BEGIN SELECT RAISE(ABORT,'mapped-queue-requires-atomic-dispatch-binding'); END;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retained reservation mapping history cannot be removed by rollback.");
}
