using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class Stage3DocumentWorkflow : Migration
{
    public const string MigrationId = "20260813154500_Stage3DocumentWorkflow";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "ContentByteLength",
            table: "document_revisions",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ContentRelativePath",
            table: "document_revisions",
            type: "TEXT",
            maxLength: 768,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ContentByteLength",
            table: "document_revisions");

        migrationBuilder.DropColumn(
            name: "ContentRelativePath",
            table: "document_revisions");
    }
}
