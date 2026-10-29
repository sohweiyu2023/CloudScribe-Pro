using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class ChirpLegacyAssociations : Migration
{
    public const string MigrationId = "20261029100000_ChirpLegacyAssociations";
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE chirp_article_legacy_associations (
            PlanSha256 TEXT NOT NULL, IdempotencyKey TEXT NOT NULL,
            AssociationJson TEXT NOT NULL, AssociationSha256 TEXT NOT NULL UNIQUE,
            FirstPolicySha256 TEXT NOT NULL, FirstSourceApprovalSha256 TEXT NOT NULL,
            PRIMARY KEY(PlanSha256,IdempotencyKey),
            FOREIGN KEY(PlanSha256) REFERENCES chirp_article_plans(PlanSha256) ON DELETE RESTRICT);
        CREATE TRIGGER chirp_legacy_association_no_update BEFORE UPDATE ON chirp_article_legacy_associations
            BEGIN SELECT RAISE(ABORT,'Historical association audit is immutable.'); END;
        CREATE TRIGGER chirp_legacy_association_no_delete BEFORE DELETE ON chirp_article_legacy_associations
            BEGIN SELECT RAISE(ABORT,'Historical association audit is retained.'); END;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Historical association provenance cannot be discarded by downgrade.");
}
