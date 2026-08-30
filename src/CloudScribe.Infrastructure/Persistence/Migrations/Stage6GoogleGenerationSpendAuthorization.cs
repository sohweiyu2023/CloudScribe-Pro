using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class Stage6GoogleGenerationSpendAuthorization : Migration
{
    public const string MigrationId = "20260830092500_Stage6GoogleGenerationSpendAuthorization";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "google_generation_spend_authorizations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                AccountId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                CredentialReferenceId = table.Column<string>(type: "TEXT", maxLength: 192, nullable: false),
                CapabilityProvenanceId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                PricingProvenanceId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                RequestRevision = table.Column<int>(type: "INTEGER", nullable: false),
                VoiceName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                AudioEncoding = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                CompiledPayloadSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                CompiledPayloadBytes = table.Column<int>(type: "INTEGER", nullable: false),
                Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                Scale = table.Column<int>(type: "INTEGER", nullable: false),
                AuthorizedMaximumMinorUnits = table.Column<long>(type: "INTEGER", nullable: false),
                ApprovedEstimateMinorUnits = table.Column<long>(type: "INTEGER", nullable: false),
                ApprovedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_google_generation_spend_authorizations", item => item.Id));

        migrationBuilder.CreateIndex(
            name: "IX_google_generation_spend_authorizations_exact_envelope",
            table: "google_generation_spend_authorizations",
            columns: new[]
            {
                "AccountId",
                "CredentialReferenceId",
                "CapabilityProvenanceId",
                "PricingProvenanceId",
                "RequestRevision",
                "VoiceName",
                "AudioEncoding",
                "CompiledPayloadSha256",
                "CompiledPayloadBytes",
            },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "google_generation_spend_authorizations");
    }
}
