using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class Stage4PricingContractOverrides : Migration
{
    public const string MigrationId = "20260817052000_Stage4PricingContractOverrides";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "pricing_contract_overrides",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Sha256 = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 64, nullable: false),
                OverrideBytes = table.Column<byte[]>(type: "BLOB", nullable: false),
                Label = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                ProvenanceId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                CapturedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_pricing_contract_overrides", item => item.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_pricing_contract_overrides_Sha256",
            table: "pricing_contract_overrides",
            column: "Sha256",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_pricing_contract_overrides_CapturedAtUnixMilliseconds",
            table: "pricing_contract_overrides",
            column: "CapturedAtUnixMilliseconds");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "pricing_contract_overrides");
    }
}
