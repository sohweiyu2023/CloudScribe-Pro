using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class Stage6GoogleGenerationQueueState : Migration
{
    public const string MigrationId = "20260906091500_Stage6GoogleGenerationQueueState";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "google_generation_queue_states",
            columns: table => new
            {
                AccountId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                OperationStableId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                UnresolvedSubmission = table.Column<bool>(type: "INTEGER", nullable: false),
                ProviderRequestId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_google_generation_queue_states",
                    item => new { item.AccountId, item.OperationStableId, item.IdempotencyKey });
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "google_generation_queue_states");
    }
}
