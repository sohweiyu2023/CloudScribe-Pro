using CloudScribe.Domain.Observability;

namespace CloudScribe.Application.Observability;

public interface IActivityTimelineStore
{
    Task AppendAsync(ActivityTimelineEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActivityTimelineEntry>> GetRecentAsync(int maximumCount, CancellationToken cancellationToken = default);
}
