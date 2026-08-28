using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class Stage3Documents : Migration
{
    public const string MigrationId = "20260812123000_Stage3Documents";

    private static readonly string[] BookmarkDocumentOffsetColumns = ["DocumentId", "GraphemeOffset"];
    private static readonly string[] RevisionDocumentCreatedColumns = ["DocumentId", "CreatedAtUnixMilliseconds"];
    private static readonly string[] SectionDocumentOrdinalColumns = ["DocumentId", "Ordinal"];
    private static readonly string[] DocumentStatusUpdatedColumns = ["Status", "UpdatedAtUnixMilliseconds"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        CreateDocuments(migrationBuilder);
        CreateTags(migrationBuilder);
        CreateBookmarks(migrationBuilder);
        CreateRevisions(migrationBuilder);
        CreateSections(migrationBuilder);
        CreateReadingPositions(migrationBuilder);
        CreateDocumentTags(migrationBuilder);
        CreateIndexes(migrationBuilder);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "bookmarks");
        migrationBuilder.DropTable(name: "document_revisions");
        migrationBuilder.DropTable(name: "document_sections");
        migrationBuilder.DropTable(name: "document_tags");
        migrationBuilder.DropTable(name: "reading_positions");
        migrationBuilder.DropTable(name: "tags");
        migrationBuilder.DropTable(name: "documents");
    }

    private static void CreateDocuments(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "documents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                DraftText = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                CurrentRevisionId = table.Column<Guid>(type: "TEXT", nullable: true),
                VoiceReference = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                PresetReference = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                ConcurrencyVersion = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_documents", item => item.Id);
            });
    }

    private static void CreateTags(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "tags",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                NormalizedName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tags", item => item.Id);
            });
    }

    private static void CreateBookmarks(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "bookmarks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                GraphemeOffset = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_bookmarks", item => item.Id);
                table.ForeignKey(
                    name: "FK_bookmarks_documents_DocumentId",
                    column: item => item.DocumentId,
                    principalTable: "documents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    private static void CreateRevisions(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "document_revisions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                CreatedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                RevisionKind = table.Column<int>(type: "INTEGER", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                ContentText = table.Column<string>(type: "TEXT", nullable: false),
                ContentSha256 = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 64, nullable: false),
                ImportProvenance = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_document_revisions", item => item.Id);
                table.ForeignKey(
                    name: "FK_document_revisions_documents_DocumentId",
                    column: item => item.DocumentId,
                    principalTable: "documents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    private static void CreateSections(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "document_sections",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                StartGraphemeOffset = table.Column<long>(type: "INTEGER", nullable: false),
                EndGraphemeOffset = table.Column<long>(type: "INTEGER", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_document_sections", item => item.Id);
                table.ForeignKey(
                    name: "FK_document_sections_documents_DocumentId",
                    column: item => item.DocumentId,
                    principalTable: "documents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    private static void CreateReadingPositions(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "reading_positions",
            columns: table => new
            {
                DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                GraphemeOffset = table.Column<long>(type: "INTEGER", nullable: false),
                ActiveSectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_reading_positions", item => item.DocumentId);
                table.ForeignKey(
                    name: "FK_reading_positions_documents_DocumentId",
                    column: item => item.DocumentId,
                    principalTable: "documents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    private static void CreateDocumentTags(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "document_tags",
            columns: table => new
            {
                DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                TagId = table.Column<Guid>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_document_tags", item => new { item.DocumentId, item.TagId });
                table.ForeignKey(
                    name: "FK_document_tags_documents_DocumentId",
                    column: item => item.DocumentId,
                    principalTable: "documents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_document_tags_tags_TagId",
                    column: item => item.TagId,
                    principalTable: "tags",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    private static void CreateIndexes(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_bookmarks_DocumentId_GraphemeOffset",
            table: "bookmarks",
            columns: BookmarkDocumentOffsetColumns);
        migrationBuilder.CreateIndex(
            name: "IX_document_revisions_DocumentId_CreatedAtUnixMilliseconds",
            table: "document_revisions",
            columns: RevisionDocumentCreatedColumns);
        migrationBuilder.CreateIndex(
            name: "IX_document_sections_DocumentId_Ordinal",
            table: "document_sections",
            columns: SectionDocumentOrdinalColumns,
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_document_tags_TagId",
            table: "document_tags",
            column: "TagId");
        migrationBuilder.CreateIndex(
            name: "IX_documents_Status_UpdatedAtUnixMilliseconds",
            table: "documents",
            columns: DocumentStatusUpdatedColumns);
        migrationBuilder.CreateIndex(
            name: "IX_documents_Title",
            table: "documents",
            column: "Title");
        migrationBuilder.CreateIndex(
            name: "IX_documents_UpdatedAtUnixMilliseconds",
            table: "documents",
            column: "UpdatedAtUnixMilliseconds");
        migrationBuilder.CreateIndex(
            name: "IX_tags_NormalizedName",
            table: "tags",
            column: "NormalizedName",
            unique: true);
    }
}
