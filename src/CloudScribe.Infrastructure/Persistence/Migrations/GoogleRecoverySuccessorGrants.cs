using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class GoogleRecoverySuccessorGrants : Migration
{
    public const string MigrationId = "20261028000100_GoogleRecoverySuccessorGrants";
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE google_recovery_grant_transitions (
            GrantSha256 TEXT NOT NULL PRIMARY KEY,
            AuthorizationId TEXT NOT NULL UNIQUE,
            SourceApprovalSha256 TEXT NOT NULL,
            LogicalItemId TEXT NOT NULL,
            EvidenceJson TEXT NOT NULL,
            UNIQUE(SourceApprovalSha256,LogicalItemId),
            FOREIGN KEY(AuthorizationId) REFERENCES google_recovery_attempts(AuthorizationId)
        );
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Recovery approval lineage cannot be discarded by downgrade.");
}
