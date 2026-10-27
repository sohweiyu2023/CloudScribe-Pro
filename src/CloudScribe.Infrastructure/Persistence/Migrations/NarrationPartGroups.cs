using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class NarrationPartGroups : Migration
{
    public const string MigrationId = "20261027011000_NarrationPartGroups";
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE narration_part_groups (
                Id TEXT NOT NULL PRIMARY KEY, ParentDocumentId TEXT NOT NULL, SourceRevisionId TEXT NOT NULL,
                SourceSha256 TEXT NOT NULL, PartitionSha256 TEXT NOT NULL, OrderedPartHashes TEXT NOT NULL,
                ExpectedPartCount INTEGER NOT NULL CHECK(ExpectedPartCount BETWEEN 1 AND 100), Complete INTEGER NOT NULL,
                FOREIGN KEY(ParentDocumentId) REFERENCES documents(Id) ON DELETE RESTRICT,
                FOREIGN KEY(SourceRevisionId) REFERENCES document_revisions(Id) ON DELETE RESTRICT,
                UNIQUE(ParentDocumentId));
            CREATE TABLE narration_part_links (
                GroupId TEXT NOT NULL, ChildDocumentId TEXT NOT NULL PRIMARY KEY, ChildRevisionId TEXT NOT NULL,
                ChildSha256 TEXT NOT NULL, Ordinal INTEGER NOT NULL CHECK(Ordinal > 0),
                FOREIGN KEY(GroupId) REFERENCES narration_part_groups(Id) ON DELETE RESTRICT,
                FOREIGN KEY(ChildDocumentId) REFERENCES documents(Id) ON DELETE RESTRICT,
                FOREIGN KEY(ChildRevisionId) REFERENCES document_revisions(Id) ON DELETE RESTRICT,
                UNIQUE(GroupId,Ordinal));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Group provenance is retained; rollback requires a reviewed metadata-preserving plan.");
}
