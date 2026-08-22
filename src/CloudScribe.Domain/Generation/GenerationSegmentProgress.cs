namespace CloudScribe.Domain.Generation;

public enum GenerationSegmentProgressState
{
    Pending,
    Submitting,
    SubmissionUnknown,
    RetryWait,
    Completed,
    Failed,
    Cancelled,
}

public sealed record GenerationSegmentProgress
{
    public GenerationSegmentProgress(
        Guid jobId,
        string segmentId,
        int segmentIndex,
        string idempotencyKey,
        GenerationSegmentProgressState state,
        int completedAttempts,
        long updatedAtUnixMilliseconds,
        long? notBeforeUnixMilliseconds = null,
        string? providerRequestId = null,
        string? cacheKeySha256 = null,
        string? diagnosticCode = null)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("Job identifier is required.", nameof(jobId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (segmentIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentIndex));
        }

        if (completedAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completedAttempts));
        }

        if (state == GenerationSegmentProgressState.Completed && string.IsNullOrWhiteSpace(cacheKeySha256))
        {
            throw new ArgumentException("Completed segments must bind the content-addressed cache key.", nameof(cacheKeySha256));
        }

        if (state == GenerationSegmentProgressState.RetryWait && notBeforeUnixMilliseconds is null)
        {
            throw new ArgumentException("Retry-wait segments require a not-before instant.", nameof(notBeforeUnixMilliseconds));
        }

        JobId = jobId;
        SegmentId = segmentId;
        SegmentIndex = segmentIndex;
        IdempotencyKey = idempotencyKey;
        State = state;
        CompletedAttempts = completedAttempts;
        UpdatedAtUnixMilliseconds = updatedAtUnixMilliseconds;
        NotBeforeUnixMilliseconds = notBeforeUnixMilliseconds;
        ProviderRequestId = providerRequestId;
        CacheKeySha256 = cacheKeySha256;
        DiagnosticCode = diagnosticCode;
    }

    public Guid JobId { get; }

    public string SegmentId { get; }

    public int SegmentIndex { get; }

    public string IdempotencyKey { get; }

    public GenerationSegmentProgressState State { get; }

    public int CompletedAttempts { get; }

    public long UpdatedAtUnixMilliseconds { get; }

    public long? NotBeforeUnixMilliseconds { get; }

    public string? ProviderRequestId { get; }

    public string? CacheKeySha256 { get; }

    public string? DiagnosticCode { get; }

    public bool RequiresReconciliation =>
        State is GenerationSegmentProgressState.Submitting or GenerationSegmentProgressState.SubmissionUnknown;

    public bool IsTerminal =>
        State is GenerationSegmentProgressState.Completed or GenerationSegmentProgressState.Failed or GenerationSegmentProgressState.Cancelled;

    public GenerationSegmentProgress MarkSubmissionStarted(long nowUnixMilliseconds) =>
        this with
        {
            State = GenerationSegmentProgressState.Submitting,
            CompletedAttempts = checked(CompletedAttempts + 1),
            UpdatedAtUnixMilliseconds = nowUnixMilliseconds,
            NotBeforeUnixMilliseconds = null,
            ProviderRequestId = null,
            DiagnosticCode = "segment.submission.started",
        };

    public GenerationSegmentProgress MarkSubmissionUnknown(long nowUnixMilliseconds, string? providerRequestId, string diagnosticCode) =>
        this with
        {
            State = GenerationSegmentProgressState.SubmissionUnknown,
            UpdatedAtUnixMilliseconds = nowUnixMilliseconds,
            NotBeforeUnixMilliseconds = null,
            ProviderRequestId = providerRequestId,
            DiagnosticCode = diagnosticCode,
        };

    public GenerationSegmentProgress MarkRetryWait(long nowUnixMilliseconds, TimeSpan delay, string diagnosticCode)
    {
        if (delay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        return this with
        {
            State = GenerationSegmentProgressState.RetryWait,
            UpdatedAtUnixMilliseconds = nowUnixMilliseconds,
            NotBeforeUnixMilliseconds = checked(nowUnixMilliseconds + (long)delay.TotalMilliseconds),
            ProviderRequestId = null,
            DiagnosticCode = diagnosticCode,
        };
    }

    public GenerationSegmentProgress MarkCompleted(long nowUnixMilliseconds, string cacheKeySha256, string? providerRequestId, string diagnosticCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKeySha256);
        return this with
        {
            State = GenerationSegmentProgressState.Completed,
            UpdatedAtUnixMilliseconds = nowUnixMilliseconds,
            NotBeforeUnixMilliseconds = null,
            ProviderRequestId = providerRequestId,
            CacheKeySha256 = cacheKeySha256,
            DiagnosticCode = diagnosticCode,
        };
    }

    public GenerationSegmentProgress MarkFailed(long nowUnixMilliseconds, string diagnosticCode) =>
        this with
        {
            State = GenerationSegmentProgressState.Failed,
            UpdatedAtUnixMilliseconds = nowUnixMilliseconds,
            NotBeforeUnixMilliseconds = null,
            DiagnosticCode = diagnosticCode,
        };
}
