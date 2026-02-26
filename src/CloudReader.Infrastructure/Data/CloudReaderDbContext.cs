using CloudReader.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudReader.Infrastructure.Data;

public sealed class CloudReaderDbContext(DbContextOptions<CloudReaderDbContext> options) : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<DocumentTag> DocumentTags => Set<DocumentTag>();
    public DbSet<VoicePreset> VoicePresets => Set<VoicePreset>();
    public DbSet<Generation> Generations => Set<Generation>();
    public DbSet<Segment> Segments => Set<Segment>();
    public DbSet<Lexicon> Lexicon => Set<Lexicon>();
    public DbSet<MonthlyUsage> MonthlyUsage => Set<MonthlyUsage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentTag>().HasKey(x => new { x.DocumentId, x.TagId });
        modelBuilder.Entity<DocumentTag>().HasOne(x => x.Document).WithMany(x => x.Tags).HasForeignKey(x => x.DocumentId);
        modelBuilder.Entity<DocumentTag>().HasOne(x => x.Tag).WithMany(x => x.Documents).HasForeignKey(x => x.TagId);
        modelBuilder.Entity<MonthlyUsage>().HasIndex(x => new { x.MonthKey, x.Tier }).IsUnique();
    }
}
