using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class Stage6GoogleGenerationProjectAuthorization : Migration
{
    public const string MigrationId = "20260906132500_Stage6GoogleGenerationProjectAuthorization";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "google_generation_project_authorizations",
            columns: table => new
            {
                AccountId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                ModelId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                CredentialReferenceId = table.Column<string>(type: "TEXT", maxLength: 192, nullable: false),
                CapabilityProvenanceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                EndpointId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                RegionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                EndpointOrigin = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                Authorized = table.Column<bool>(type: "INTEGER", nullable: false),
                CapturedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                ExpiresAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_google_generation_project_authorizations",
                    item => new { item.AccountId, item.ProjectId, item.ModelId });
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "google_generation_project_authorizations");
    }
}
