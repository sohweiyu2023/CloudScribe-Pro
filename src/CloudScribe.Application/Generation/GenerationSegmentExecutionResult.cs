using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed record GenerationSegmentExecutionResult(
    bool CacheHit,
    SubmissionDisposition Disposition,
    string? ProviderRequestId,
    ReadOnlyMemory<byte> MediaBytes,
    string DiagnosticCode,
    TimeSpan? RetryAfter,
    ContentAddressedSegmentKey CacheKey)
{
    public bool RequiresReconciliation => Disposition == SubmissionDisposition.UnknownRequiresReconciliation;
}
