using CloudScribe.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Persistence;

public sealed class CloudScribeDbContext(DbContextOptions<CloudScribeDbContext> options) : DbContext(options)
{
    public DbSet<ActivityTimelineEntity> ActivityTimeline => Set<ActivityTimelineEntity>();

    public DbSet<BillableLedgerEntity> BillableLedger => Set<BillableLedgerEntity>();

    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();

    public DbSet<DocumentRevisionEntity> DocumentRevisions => Set<DocumentRevisionEntity>();

    public DbSet<DocumentSectionEntity> DocumentSections => Set<DocumentSectionEntity>();

    public DbSet<TagEntity> Tags => Set<TagEntity>();

    public DbSet<DocumentTagEntity> DocumentTags => Set<DocumentTagEntity>();

    public DbSet<BookmarkEntity> Bookmarks => Set<BookmarkEntity>();

    public DbSet<ReadingPositionEntity> ReadingPositions => Set<ReadingPositionEntity>();

    public DbSet<PricingCatalogSnapshotEntity> PricingCatalogSnapshots => Set<PricingCatalogSnapshotEntity>();

    public DbSet<PricingCatalogActivationEntity> PricingCatalogActivations => Set<PricingCatalogActivationEntity>();

    public DbSet<PricingContractOverrideEntity> PricingContractOverrides => Set<PricingContractOverrideEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureObservability(modelBuilder);
        ConfigureDocuments(modelBuilder);
        ConfigurePricingCatalogHistory(modelBuilder);
        ConfigurePricingContractOverrides(modelBuilder);
    }

    private static void ConfigureObservability(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ActivityTimelineEntity>(entity =>
        {
            entity.ToTable("activity_timeline");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.EventCode).HasMaxLength(80).IsRequired();
            entity.Property(item => item.Summary).HasMaxLength(240).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(96).IsRequired();
            entity.HasIndex(item => item.OccurredAtUnixMilliseconds);
        });

        modelBuilder.Entity<BillableLedgerEntity>(entity =>
        {
            entity.ToTable("billable_operation_ledger");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.SnapshotId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.CurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(96).IsRequired();
            entity.Property(item => item.ProviderRequestId).HasMaxLength(160);
            entity.Property(item => item.EventCode).HasMaxLength(80).IsRequired();
            entity.HasIndex(item => new { item.OperationId, item.OccurredAtUnixMilliseconds });
        });
    }

    private static void ConfigurePricingCatalogHistory(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PricingCatalogSnapshotEntity>(entity =>
        {
            entity.ToTable("pricing_catalog_snapshots");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(item => item.CatalogBytes).IsRequired();
            entity.Property(item => item.SourceLabel).HasMaxLength(240).IsRequired();
            entity.Property(item => item.SignatureKeyId).HasMaxLength(160);
            entity.HasIndex(item => item.Sha256).IsUnique();
            entity.HasIndex(item => item.CapturedAtUnixMilliseconds);
        });

        modelBuilder.Entity<PricingCatalogActivationEntity>(entity =>
        {
            entity.ToTable("pricing_catalog_activations");
            entity.HasKey(item => item.Sequence);
            entity.Property(item => item.Sequence).ValueGeneratedOnAdd();
            entity.Property(item => item.Reason).HasMaxLength(240).IsRequired();
            entity.HasIndex(item => item.SnapshotId);
            entity.HasIndex(item => item.OccurredAtUnixMilliseconds);
            entity.HasOne<PricingCatalogSnapshotEntity>()
                .WithMany()
                .HasForeignKey(item => item.SnapshotId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigurePricingContractOverrides(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PricingContractOverrideEntity>(entity =>
        {
            entity.ToTable("pricing_contract_overrides");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(item => item.OverrideBytes).IsRequired();
            entity.Property(item => item.Label).HasMaxLength(240).IsRequired();
            entity.Property(item => item.ProvenanceId).HasMaxLength(160).IsRequired();
            entity.HasIndex(item => item.Sha256).IsUnique();
            entity.HasIndex(item => item.CapturedAtUnixMilliseconds);
        });
    }

    private static void ConfigureDocuments(ModelBuilder modelBuilder)
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
            entity.Property(item => item.ContentRelativePath).HasMaxLength(768);
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
}
