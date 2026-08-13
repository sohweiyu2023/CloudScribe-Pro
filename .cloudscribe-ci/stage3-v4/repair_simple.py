from pathlib import Path

root = Path('source')

store = root / 'src/CloudScribe.Infrastructure/Files/DocumentContentStore.cs'
text = store.read_text(encoding='utf-8')
record = 'public sealed record DocumentContentCommit(string RelativePath, string Sha256, long ByteLength);\n\n'
if text.count(record) != 1:
    raise SystemExit('DocumentContentCommit preimage mismatch')
text = text.replace(record, '', 1)

await_using_store = '            await using (FileStream stream = new('
if text.count(await_using_store) != 1:
    raise SystemExit('DocumentContentStore async-disposal preimage mismatch')
text = text.replace(await_using_store, '            using (FileStream stream = new(', 1)

equality_old = '        if (relativePath == "..")'
if text.count(equality_old) != 1:
    raise SystemExit('DocumentContentStore equality preimage mismatch')
text = text.replace(
    equality_old,
    '        if (string.Equals(relativePath, "..", StringComparison.Ordinal))',
    1,
)
store.write_text(text, encoding='utf-8')
(store.parent / 'DocumentContentCommit.cs').write_text(
    'namespace CloudScribe.Infrastructure.Files;\n\n'
    'public sealed record DocumentContentCommit(string RelativePath, string Sha256, long ByteLength);\n',
    encoding='utf-8',
)

bridge = root / 'src/CloudScribe.Infrastructure/Persistence/LegacyDatabaseMigrationBridge.cs'
text = bridge.read_text(encoding='utf-8')
old = '    public async Task RecoverAbandonedEfMigrationLockAsync('
if text.count(old) != 1:
    raise SystemExit('lock recovery signature mismatch')
bridge.write_text(
    text.replace(old, '    public static async Task RecoverAbandonedEfMigrationLockAsync(', 1),
    encoding='utf-8',
)

for rel, old_call in (
    (
        'src/CloudScribe.Infrastructure/Persistence/DatabaseInitializer.cs',
        'legacyMigrationBridge.RecoverAbandonedEfMigrationLockAsync',
    ),
    (
        'tests/CloudScribe.Infrastructure.Tests/Stage3MigrationTests.cs',
        'bridge.RecoverAbandonedEfMigrationLockAsync',
    ),
):
    p = root / rel
    t = p.read_text(encoding='utf-8')
    if t.count(old_call) != 1:
        raise SystemExit(f'call preimage mismatch: {rel}')
    p.write_text(
        t.replace(old_call, 'LegacyDatabaseMigrationBridge.RecoverAbandonedEfMigrationLockAsync', 1),
        encoding='utf-8',
    )

initializer = root / 'src/CloudScribe.Infrastructure/Persistence/DatabaseInitializer.cs'
text = initializer.read_text(encoding='utf-8')
if text.count('await using') != 9:
    raise SystemExit(f'DatabaseInitializer async-disposal preimage mismatch: {text.count("await using")}')
initializer.write_text(text.replace('await using', 'using'), encoding='utf-8')

context = root / 'src/CloudScribe.Infrastructure/Persistence/CloudScribeDbContext.cs'
text = context.read_text(encoding='utf-8')
start = text.index('    private static void ConfigureDocuments(ModelBuilder modelBuilder)')
class_end = text.rfind('\n}')
replacement = '''    private static void ConfigureDocuments(ModelBuilder modelBuilder)
    {
        ConfigureDocument(modelBuilder);
        ConfigureRevision(modelBuilder);
        ConfigureSection(modelBuilder);
        ConfigureTags(modelBuilder);
        ConfigureBookmarksAndReadingPosition(modelBuilder);
    }

    private static void ConfigureDocument(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentEntity>(entity =>
        {
            entity.ToTable("documents");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Title).HasMaxLength(240).IsRequired();
            entity.Property(item => item.DraftText).IsRequired();
            entity.Property(item => item.VoiceReference).HasMaxLength(240);
            entity.Property(item => item.PresetReference).HasMaxLength(240);
            entity.Property(item => item.ConcurrencyVersion).IsConcurrencyToken();
            entity.HasIndex(item => item.UpdatedAtUnixMilliseconds);
            entity.HasIndex(item => new { item.Status, item.UpdatedAtUnixMilliseconds });
            entity.HasIndex(item => item.Title);
        });
    }

    private static void ConfigureRevision(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentRevisionEntity>(entity =>
        {
            entity.ToTable("document_revisions");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(240);
            entity.Property(item => item.ContentText).IsRequired();
            entity.Property(item => item.ContentSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(item => item.ImportProvenance).HasMaxLength(2048);
            entity.HasIndex(item => new { item.DocumentId, item.CreatedAtUnixMilliseconds });
            entity.HasOne<DocumentEntity>()
                .WithMany()
                .HasForeignKey(item => item.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureSection(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentSectionEntity>(entity =>
        {
            entity.ToTable("document_sections");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Title).HasMaxLength(240).IsRequired();
            entity.HasIndex(item => new { item.DocumentId, item.Ordinal }).IsUnique();
            entity.HasOne<DocumentEntity>()
                .WithMany()
                .HasForeignKey(item => item.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureTags(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TagEntity>(entity =>
        {
            entity.ToTable("tags");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.NormalizedName).HasMaxLength(120).IsRequired();
            entity.HasIndex(item => item.NormalizedName).IsUnique();
        });

        modelBuilder.Entity<DocumentTagEntity>(entity =>
        {
            entity.ToTable("document_tags");
            entity.HasKey(item => new { item.DocumentId, item.TagId });
            entity.HasOne<DocumentEntity>()
                .WithMany()
                .HasForeignKey(item => item.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<TagEntity>()
                .WithMany()
                .HasForeignKey(item => item.TagId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureBookmarksAndReadingPosition(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BookmarkEntity>(entity =>
        {
            entity.ToTable("bookmarks");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(240).IsRequired();
            entity.HasIndex(item => new { item.DocumentId, item.GraphemeOffset });
            entity.HasOne<DocumentEntity>()
                .WithMany()
                .HasForeignKey(item => item.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReadingPositionEntity>(entity =>
        {
            entity.ToTable("reading_positions");
            entity.HasKey(item => item.DocumentId);
            entity.HasOne<DocumentEntity>()
                .WithOne()
                .HasForeignKey<ReadingPositionEntity>(item => item.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
'''
context.write_text(text[:start] + replacement + text[class_end:], encoding='utf-8')

v = root / 'tools/verify_stage3_source.py'
t = v.read_text(encoding='utf-8')
for old, new in (
    (
        'src/CloudScribe.Infrastructure/Persistence/Migrations/20260812122000_Stage2Baseline.cs',
        'src/CloudScribe.Infrastructure/Persistence/Migrations/Stage2Baseline.cs',
    ),
    (
        'src/CloudScribe.Infrastructure/Persistence/Migrations/20260812123000_Stage3Documents.cs',
        'src/CloudScribe.Infrastructure/Persistence/Migrations/Stage3Documents.cs',
    ),
):
    if t.count(old) != 1:
        raise SystemExit(f'verifier preimage mismatch: {old}')
    t = t.replace(old, new)
needle = '        "src/CloudScribe.Infrastructure/Files/DocumentContentStore.cs",\n'
if t.count(needle) < 1:
    raise SystemExit('verifier store preimage mismatch')
v.write_text(
    t.replace(
        needle,
        needle + '        "src/CloudScribe.Infrastructure/Files/DocumentContentCommit.cs",\n',
        1,
    ),
    encoding='utf-8',
)

print('CLOUDSCRIBE_STAGE3_SIMPLE_REPAIR=PASS')
