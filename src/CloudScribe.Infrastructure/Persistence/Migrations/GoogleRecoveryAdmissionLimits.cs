using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class GoogleRecoveryAdmissionLimits : Migration
{
    public const string MigrationId = "20261027140000_GoogleRecoveryAdmissionLimits";
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE google_recovery_attempts ADD COLUMN GrantMaximumNewAttempts INTEGER NULL CHECK(GrantMaximumNewAttempts > 0);
        ALTER TABLE google_recovery_attempts ADD COLUMN GrantMaximumAdditionalMinorUnits INTEGER NULL CHECK(GrantMaximumAdditionalMinorUnits >= 0);
        CREATE INDEX IX_google_recovery_admission ON google_recovery_attempts(AdmissionSha256);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Recovery grant limits and financial history cannot be discarded by downgrade.");
}
