namespace CloudScribe.Domain.Generation;

public sealed class GenerationSubmissionRecord
{
    public GenerationSubmissionRecord(
        string idempotencyKey,
        SubmissionDisposition disposition,
        string? providerRequestId,
        long recordedAtUnixMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (disposition == SubmissionDisposition.Accepted && string.IsNullOrWhiteSpace(providerRequestId))
        {
            throw new ArgumentException("Accepted submissions require a provider request identifier.", nameof(providerRequestId));
        }

        IdempotencyKey = idempotencyKey;
        Disposition = disposition;
        ProviderRequestId = providerRequestId;
        RecordedAtUnixMilliseconds = recordedAtUnixMilliseconds;
    }

    public string IdempotencyKey { get; }

    public SubmissionDisposition Disposition { get; }

    public string? ProviderRequestId { get; }

    public long RecordedAtUnixMilliseconds { get; }

    public bool RequiresReconciliation => Disposition == SubmissionDisposition.UnknownRequiresReconciliation;
}
