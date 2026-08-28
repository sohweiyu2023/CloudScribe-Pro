using CloudScribe.Domain.Observability;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed class ObservabilityPersistenceTests
{
    [Fact]
    public async Task TimelineAndBillableLedgerRoundTripExactValues()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string databasePath = Path.Combine(root, "observability.db");
        DbContextOptions<CloudScribeDbContext> options = new DbContextOptionsBuilder<CloudScribeDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        TestContextFactory factory = new(options);
        await using (CloudScribeDbContext setup = factory.CreateDbContext())
        {
            await setup.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        EfActivityTimelineStore timelineStore = new(factory);
        EfBillableOperationLedger ledger = new(factory);
        DateTimeOffset instant = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        FixedTimeProvider timeProvider = new(instant);
        ActivityTimelineEntry timeline = ActivityTimelineEntry.Create(
            timeProvider,
            ActivitySeverity.Information,
            "TEST_EVENT",
            "Recorded.",
            "correlation");
        BillableOperationLedgerEntry billable = BillableOperationLedgerEntry.Create(
            timeProvider,
            Guid.NewGuid(),
            "snapshot-1",
            BillableLedgerEventKind.EstimateApproved,
            new ExactMoney(12345, 2, "SGD"),
            "correlation",
            null,
            "ESTIMATE_APPROVED");

        await timelineStore.AppendAsync(timeline, TestContext.Current.CancellationToken);
        await ledger.AppendRequiredAsync(billable, TestContext.Current.CancellationToken);
        ActivityTimelineEntry storedTimeline = Assert.Single(await timelineStore.GetRecentAsync(10, TestContext.Current.CancellationToken));
        BillableOperationLedgerEntry storedBillable = Assert.Single(await ledger.GetRecentAsync(10, TestContext.Current.CancellationToken));

        Assert.Equal(timeline, storedTimeline);
        Assert.Equal(billable, storedBillable);

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }


    [Fact]
    public void BillableLedgerRejectsDefaultExactMoney()
    {
        FixedTimeProvider timeProvider = new(new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero));

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            BillableOperationLedgerEntry.Create(
                timeProvider,
                Guid.NewGuid(),
                "snapshot-default-money",
                BillableLedgerEventKind.EstimateApproved,
                default,
                "correlation",
                null,
                "ESTIMATE_APPROVED"));

        Assert.Equal("amount", exception.ParamName);
    }

    [Fact]
    public void DefaultExactMoneyCannotBeConvertedSilently()
    {
        Assert.Throws<ArgumentException>(() => default(ExactMoney).ToDecimal());
    }

    private sealed class TestContextFactory(DbContextOptions<CloudScribeDbContext> options)
        : IDbContextFactory<CloudScribeDbContext>
    {
        public CloudScribeDbContext CreateDbContext() => new(options);

        public Task<CloudScribeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
