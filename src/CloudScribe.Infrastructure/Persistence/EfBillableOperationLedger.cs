using CloudScribe.Application.Observability;
using CloudScribe.Domain.Observability;
using CloudScribe.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Persistence;

public sealed class EfBillableOperationLedger(IDbContextFactory<ObservabilityDbContext> contextFactory) : IBillableOperationLedger
{
    public async Task AppendRequiredAsync(BillableOperationLedgerEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using ObservabilityDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        context.BillableLedger.Add(new BillableLedgerEntity
        {
            Id = entry.Id,
            OperationId = entry.OperationId,
            SnapshotId = entry.SnapshotId,
            EventKind = (int)entry.EventKind,
            OccurredAtUnixMilliseconds = entry.OccurredAtUtc.ToUnixTimeMilliseconds(),
            AmountUnits = entry.Amount.Units,
            AmountScale = entry.Amount.Scale,
            CurrencyCode = entry.Amount.CurrencyCode,
            CorrelationId = entry.CorrelationId,
            ProviderRequestId = entry.ProviderRequestId,
            EventCode = entry.EventCode,
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BillableOperationLedgerEntry>> GetRecentAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        using ObservabilityDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.BillableLedger
            .AsNoTracking()
            .OrderByDescending(item => item.OccurredAtUnixMilliseconds)
            .Take(maximumCount)
            .Select(item => new BillableOperationLedgerEntry(
                item.Id,
                item.OperationId,
                item.SnapshotId,
                (BillableLedgerEventKind)item.EventKind,
                DateTimeOffset.FromUnixTimeMilliseconds(item.OccurredAtUnixMilliseconds),
                new ExactMoney(item.AmountUnits, item.AmountScale, item.CurrencyCode),
                item.CorrelationId,
                item.ProviderRequestId,
                item.EventCode))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
