using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class Stage2Baseline : Migration
{
    public const string MigrationId = "20260812122000_Stage2Baseline";

    private static readonly string[] BillableOperationTimeIndexColumns = ["OperationId", "OccurredAtUnixMilliseconds"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "activity_timeline",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                OccurredAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                Severity = table.Column<int>(type: "INTEGER", nullable: false),
                EventCode = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                Summary = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                CorrelationId = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_activity_timeline", item => item.Id);
            });

        migrationBuilder.CreateTable(
            name: "billable_operation_ledger",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                SnapshotId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                EventKind = table.Column<int>(type: "INTEGER", nullable: false),
                OccurredAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                AmountUnits = table.Column<long>(type: "INTEGER", nullable: false),
                AmountScale = table.Column<int>(type: "INTEGER", nullable: false),
                CurrencyCode = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 3, nullable: false),
                CorrelationId = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                ProviderRequestId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                EventCode = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_billable_operation_ledger", item => item.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_activity_timeline_OccurredAtUnixMilliseconds",
            table: "activity_timeline",
            column: "OccurredAtUnixMilliseconds");

        migrationBuilder.CreateIndex(
            name: "IX_billable_operation_ledger_OperationId_OccurredAtUnixMilliseconds",
            table: "billable_operation_ledger",
            columns: BillableOperationTimeIndexColumns);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "activity_timeline");
        migrationBuilder.DropTable(name: "billable_operation_ledger");
    }
}
