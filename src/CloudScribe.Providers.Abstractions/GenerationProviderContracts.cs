using CloudScribe.Domain.Generation;

namespace CloudScribe.Providers.Abstractions;

public sealed class GenerationProviderRequest
{
    public GenerationProviderRequest(
        string providerStableId,
        string operationStableId,
        string accountId,
        string idempotencyKey,
        ReadOnlyMemory<byte> compiledPayload,
        string outputFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFormat);
        if (compiledPayload.IsEmpty)
        {
            throw new ArgumentException("Compiled payload is required.", nameof(compiledPayload));
        }

        ProviderStableId = providerStableId;
        OperationStableId = operationStableId;
        AccountId = accountId;
        IdempotencyKey = idempotencyKey;
        CompiledPayload = compiledPayload;
        OutputFormat = outputFormat;
    }

    public string ProviderStableId { get; }

    public string OperationStableId { get; }

    public string AccountId { get; }

    public string IdempotencyKey { get; }

    public ReadOnlyMemory<byte> CompiledPayload { get; }

    public string OutputFormat { get; }
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
