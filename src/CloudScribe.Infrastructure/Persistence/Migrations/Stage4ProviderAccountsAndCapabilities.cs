using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class Stage4ProviderAccountsAndCapabilities : Migration
{
    private static readonly string[] ProviderAccountPrincipalColumns = ["ProviderStableId", "AccountId"];
    private static readonly string[] ProviderCapabilitySnapshotIndexColumns = ["ProviderStableId", "AccountId", "CapturedAtUnixMilliseconds"];

    public const string MigrationId = "20260817061000_Stage4ProviderAccountsAndCapabilities";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        CreateProviderAccounts(migrationBuilder);
        CreateProviderCapabilitySnapshots(migrationBuilder);
        CreateProviderCapabilityEntries(migrationBuilder);
        CreateIndexes(migrationBuilder);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "provider_capability_entries");
        migrationBuilder.DropTable(name: "provider_capability_snapshots");
        migrationBuilder.DropTable(name: "provider_accounts");
    }

    private static void CreateProviderAccounts(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "provider_accounts",
            columns: table => new
            {
                ProviderStableId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                AccountId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                DisplayName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                CredentialTargetName = table.Column<string>(type: "TEXT", maxLength: 192, nullable: true),
                EndpointId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                RegionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_provider_accounts", item => new { item.ProviderStableId, item.AccountId });
            });
    }

    private static void CreateProviderCapabilitySnapshots(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "provider_capability_snapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProviderStableId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                AccountId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                AccountDisplayName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                CredentialTargetName = table.Column<string>(type: "TEXT", maxLength: 192, nullable: true),
                EndpointId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                RegionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                CapturedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                ExpiresAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                ProvenanceId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_provider_capability_snapshots", item => item.Id);
                table.ForeignKey(
                    name: "FK_provider_capability_snapshots_provider_accounts_ProviderStableId_AccountId",
                    columns: item => new { item.ProviderStableId, item.AccountId },
                    principalTable: "provider_accounts",
                    principalColumns: ProviderAccountPrincipalColumns,
                    onDelete: ReferentialAction.Restrict);
            });
    }

    private static void CreateProviderCapabilityEntries(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "provider_capability_entries",
            columns: table => new
            {
                SnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                CapabilityId = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                State = table.Column<int>(type: "INTEGER", nullable: false),
                LifecycleState = table.Column<int>(type: "INTEGER", nullable: false),
                DisabledReason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_provider_capability_entries", item => new { item.SnapshotId, item.CapabilityId });
                table.ForeignKey(
                    name: "FK_provider_capability_entries_provider_capability_snapshots_SnapshotId",
                    column: item => item.SnapshotId,
                    principalTable: "provider_capability_snapshots",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    private static void CreateIndexes(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_provider_accounts_UpdatedAtUnixMilliseconds",
            table: "provider_accounts",
            column: "UpdatedAtUnixMilliseconds");
        migrationBuilder.CreateIndex(
            name: "IX_provider_capability_snapshots_ProviderStableId_AccountId_CapturedAtUnixMilliseconds",
            table: "provider_capability_snapshots",
            columns: ProviderCapabilitySnapshotIndexColumns);
    }
}
