using CloudScribe.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Persistence;

public sealed class ObservabilityDbContext(DbContextOptions<ObservabilityDbContext> options) : DbContext(options)
{
    public DbSet<ActivityTimelineEntity> ActivityTimeline => Set<ActivityTimelineEntity>();

    public DbSet<BillableLedgerEntity> BillableLedger => Set<BillableLedgerEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
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
}
