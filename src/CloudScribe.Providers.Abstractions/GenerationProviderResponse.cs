using CloudScribe.Domain.Generation;

namespace CloudScribe.Providers.Abstractions;

public sealed record GenerationProviderResponse(
    SubmissionDisposition Disposition,
    string? ProviderRequestId,
    ReadOnlyMemory<byte> MediaBytes,
    string? MediaContentType,
    TimeSpan? RetryAfter,
    string DiagnosticCode)
{
    public bool IsAccepted => Disposition == SubmissionDisposition.Accepted;
}
