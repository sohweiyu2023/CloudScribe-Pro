using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class Stage4PricingCatalogHistory : Migration
{
    public const string MigrationId = "20260817004500_Stage4PricingCatalogHistory";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "pricing_catalog_snapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Sha256 = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 64, nullable: false),
                CatalogBytes = table.Column<byte[]>(type: "BLOB", nullable: false),
                TrustState = table.Column<int>(type: "INTEGER", nullable: false),
                SourceKind = table.Column<int>(type: "INTEGER", nullable: false),
                SourceLabel = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                CapturedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                SignatureKeyId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_pricing_catalog_snapshots", item => item.Id);
            });

        migrationBuilder.CreateTable(
            name: "pricing_catalog_activations",
            columns: table => new
            {
                Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                PreviousSnapshotId = table.Column<Guid>(type: "TEXT", nullable: true),
                Kind = table.Column<int>(type: "INTEGER", nullable: false),
                ApprovalKind = table.Column<int>(type: "INTEGER", nullable: false),
                Reason = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                OccurredAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_pricing_catalog_activations", item => item.Sequence);
                table.ForeignKey(
                    name: "FK_pricing_catalog_activations_pricing_catalog_snapshots_SnapshotId",
                    column: item => item.SnapshotId,
                    principalTable: "pricing_catalog_snapshots",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_pricing_catalog_snapshots_Sha256",
            table: "pricing_catalog_snapshots",
            column: "Sha256",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_pricing_catalog_snapshots_CapturedAtUnixMilliseconds",
            table: "pricing_catalog_snapshots",
            column: "CapturedAtUnixMilliseconds");
        migrationBuilder.CreateIndex(
            name: "IX_pricing_catalog_activations_SnapshotId",
            table: "pricing_catalog_activations",
            column: "SnapshotId");
        migrationBuilder.CreateIndex(
            name: "IX_pricing_catalog_activations_OccurredAtUnixMilliseconds",
            table: "pricing_catalog_activations",
            column: "OccurredAtUnixMilliseconds");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "pricing_catalog_activations");
        migrationBuilder.DropTable(name: "pricing_catalog_snapshots");
    }
}
