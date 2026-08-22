using CloudScribe.Domain.Generation;

namespace CloudScribe.Providers.Abstractions;

public sealed record GenerationProviderRequest(
    string ProviderStableId,
    string OperationStableId,
    string AccountId,
    string IdempotencyKey,
    ReadOnlyMemory<byte> CompiledPayload,
    string OutputFormat)
{
    public GenerationProviderRequest
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(OperationStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(IdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputFormat);
        if (CompiledPayload.IsEmpty)
        {
            throw new ArgumentException("Compiled payload is required.", nameof(CompiledPayload));
        }
    }
}

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

public interface IGenerationProvider
{
    string ProviderStableId { get; }

    Task<GenerationProviderResponse> SubmitAsync(GenerationProviderRequest request, CancellationToken cancellationToken);

    Task<GenerationProviderResponse?> ReconcileAsync(string idempotencyKey, CancellationToken cancellationToken);
}
