namespace CloudScribe.Domain.Generation;

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
        ArgumentOutOfRangeException.ThrowIfNegative(segmentIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(completedAttempts);

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

    public Guid JobId { get; init; }

    public string SegmentId { get; init; }

    public int SegmentIndex { get; init; }

    public string IdempotencyKey { get; init; }

    public GenerationSegmentProgressState State { get; init; }

    public int CompletedAttempts { get; init; }

    public long UpdatedAtUnixMilliseconds { get; init; }

    public long? NotBeforeUnixMilliseconds { get; init; }

    public string? ProviderRequestId { get; init; }

    public string? CacheKeySha256 { get; init; }

    public string? DiagnosticCode { get; init; }

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
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero);

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
