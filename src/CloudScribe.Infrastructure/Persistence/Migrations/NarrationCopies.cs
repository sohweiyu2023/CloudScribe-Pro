using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class NarrationCopies : Migration
{
    public const string MigrationId = "20261108160000_NarrationCopies";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE batch_source_imports (
            BatchId TEXT NOT NULL, ItemId TEXT NOT NULL, RawSha256 TEXT NOT NULL,
            SourceTitle TEXT NOT NULL, SourceDocumentId TEXT NOT NULL UNIQUE,
            SourceRevisionId TEXT NOT NULL UNIQUE, SourceTextSha256 TEXT NOT NULL,
            CreatedAtUnixMilliseconds INTEGER NOT NULL,
            PRIMARY KEY(BatchId,ItemId),
            FOREIGN KEY(SourceDocumentId) REFERENCES documents(Id) ON DELETE RESTRICT,
            FOREIGN KEY(SourceRevisionId) REFERENCES document_revisions(Id) ON DELETE RESTRICT);
        CREATE TABLE narration_copies (
            SourceDocumentId TEXT NOT NULL, SourceRevisionId TEXT NOT NULL, SourceTextSha256 TEXT NOT NULL,
            SourceContentRelativePath TEXT NULL, SourceContentByteLength INTEGER NULL,
            TransformVersion TEXT NOT NULL, TransformSettings TEXT NOT NULL,
            ChildDocumentId TEXT NOT NULL UNIQUE, ChildRevisionId TEXT NOT NULL UNIQUE,
            ChildTextSha256 TEXT NOT NULL, OmittedUrlCount INTEGER NOT NULL CHECK(OmittedUrlCount > 0),
            CreatedAtUnixMilliseconds INTEGER NOT NULL,
            PRIMARY KEY(SourceDocumentId,SourceRevisionId,TransformVersion,TransformSettings),
            CHECK(SourceDocumentId <> ChildDocumentId),
            FOREIGN KEY(SourceDocumentId) REFERENCES documents(Id) ON DELETE RESTRICT,
            FOREIGN KEY(SourceRevisionId) REFERENCES document_revisions(Id) ON DELETE RESTRICT,
            FOREIGN KEY(ChildDocumentId) REFERENCES documents(Id) ON DELETE RESTRICT,
            FOREIGN KEY(ChildRevisionId) REFERENCES document_revisions(Id) ON DELETE RESTRICT);
        CREATE TRIGGER batch_source_imports_immutable BEFORE UPDATE ON batch_source_imports
        BEGIN SELECT RAISE(ABORT,'batch-source-import-is-immutable'); END;
        CREATE TRIGGER batch_source_imports_retained BEFORE DELETE ON batch_source_imports
        BEGIN SELECT RAISE(ABORT,'batch-source-import-is-retained'); END;
        CREATE TRIGGER narration_copies_immutable BEFORE UPDATE ON narration_copies
        BEGIN SELECT RAISE(ABORT,'narration-copy-provenance-is-immutable'); END;
        CREATE TRIGGER narration_copies_retained BEFORE DELETE ON narration_copies
        BEGIN SELECT RAISE(ABORT,'narration-copy-provenance-is-retained'); END;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Narration copy provenance is retained; rollback requires a metadata-preserving plan.");
}
