namespace CloudScribe.Domain.Observability;

public sealed record ActivityTimelineEntry
{
    public ActivityTimelineEntry(
        Guid id,
        DateTimeOffset occurredAtUtc,
        ActivitySeverity severity,
        string eventCode,
        string summary,
        string correlationId)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Entry ID is required.", nameof(id));
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timeline instants must be expressed in UTC.", nameof(occurredAtUtc));
        }

        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        Id = id;
        OccurredAtUtc = occurredAtUtc;
        Severity = severity;
        EventCode = RequiredToken(eventCode, 80);
        Summary = RequiredToken(summary, 240);
        CorrelationId = RequiredToken(correlationId, 96);
    }

    public Guid Id { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public ActivitySeverity Severity { get; }

    public string EventCode { get; }

    public string Summary { get; }

    public string CorrelationId { get; }

    public static ActivityTimelineEntry Create(
        TimeProvider timeProvider,
        ActivitySeverity severity,
        string eventCode,
        string summary,
        string correlationId)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return new ActivityTimelineEntry(
            Guid.NewGuid(),
            timeProvider.GetUtcNow(),
            severity,
            eventCode,
            summary,
            correlationId);
    }

    private static string RequiredToken(string value, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Value exceeds {maximumLength} characters.");
        }

        return normalized;
    }
}
