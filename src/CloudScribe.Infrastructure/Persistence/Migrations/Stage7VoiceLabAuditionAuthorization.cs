using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class Stage7VoiceLabAuditionAuthorization : Migration
{
    public const string MigrationId = "20260902231500_Stage7VoiceLabAuditionAuthorization";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "voice_lab_audition_authorizations",
            columns: table => new
            {
                ProviderId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                AccountId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                VoiceId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                VoiceFingerprint = table.Column<string>(type: "TEXT", maxLength: 192, nullable: false),
                CapabilityEvidenceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                CredentialReferenceId = table.Column<string>(type: "TEXT", maxLength: 192, nullable: false),
                PricingEvidenceId = table.Column<string>(type: "TEXT", maxLength: 192, nullable: false),
                SpendAuthorizationId = table.Column<string>(type: "TEXT", maxLength: 192, nullable: false),
                AccountRevision = table.Column<long>(type: "INTEGER", nullable: false),
                CapturedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                ExpiresAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_voice_lab_audition_authorizations",
                    item => new { item.ProviderId, item.AccountId, item.ProjectId, item.VoiceId });
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "voice_lab_audition_authorizations");
    }
}
