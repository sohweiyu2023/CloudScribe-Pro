using CloudScribe.Application.Observability;
using CloudScribe.Domain.Observability;
using CloudScribe.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Persistence;

public sealed class EfActivityTimelineStore(IDbContextFactory<ObservabilityDbContext> contextFactory) : IActivityTimelineStore
{
    public async Task AppendAsync(ActivityTimelineEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using ObservabilityDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        context.ActivityTimeline.Add(new ActivityTimelineEntity
        {
            Id = entry.Id,
            OccurredAtUnixMilliseconds = entry.OccurredAtUtc.ToUnixTimeMilliseconds(),
            Severity = (int)entry.Severity,
            EventCode = entry.EventCode,
            Summary = entry.Summary,
            CorrelationId = entry.CorrelationId,
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ActivityTimelineEntry>> GetRecentAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        using ObservabilityDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.ActivityTimeline
            .AsNoTracking()
            .OrderByDescending(item => item.OccurredAtUnixMilliseconds)
            .Take(maximumCount)
            .Select(item => new ActivityTimelineEntry(
                item.Id,
                DateTimeOffset.FromUnixTimeMilliseconds(item.OccurredAtUnixMilliseconds),
                (ActivitySeverity)item.Severity,
                item.EventCode,
                item.Summary,
                item.CorrelationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
