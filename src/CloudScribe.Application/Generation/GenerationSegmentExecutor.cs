using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Generation;

public sealed record GenerationSegmentExecutionRequest(
    string ProviderStableId,
    string OperationStableId,
    string AccountId,
    string VoiceStableId,
    string CompilationProfileId,
    string IdempotencyKey,
    ReadOnlyMemory<byte> CompiledPayload,
    string OutputFormat);

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

public sealed class GenerationSegmentExecutor
{
    private readonly IGenerationProvider _provider;
    private readonly IGenerationSegmentCache _cache;

    public GenerationSegmentExecutor(IGenerationProvider provider, IGenerationSegmentCache cache)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<GenerationSegmentExecutionResult> ExecuteAsync(
        GenerationSegmentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateProvider(request.ProviderStableId);

        var key = ContentAddressedSegmentKey.Create(
            request.CompiledPayload.Span,
            request.ProviderStableId,
            request.OperationStableId,
            request.VoiceStableId,
            request.CompilationProfileId);

        var cached = await _cache.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (cached is { Length: > 0 })
        {
            return new GenerationSegmentExecutionResult(
                true,
                SubmissionDisposition.Accepted,
                null,
                cached,
                "segment.cache.hit",
                null,
                key);
        }

        var providerRequest = new GenerationProviderRequest(
            request.ProviderStableId,
            request.OperationStableId,
            request.AccountId,
            request.IdempotencyKey,
            request.CompiledPayload,
            request.OutputFormat);

        var response = await _provider.SubmitAsync(providerRequest, cancellationToken).ConfigureAwait(false);
        if (response.Disposition == SubmissionDisposition.Accepted && !response.MediaBytes.IsEmpty)
        {
            await _cache.StoreAsync(key, response.MediaBytes, cancellationToken).ConfigureAwait(false);
        }

        return FromProviderResponse(false, key, response);
    }

    public async Task<GenerationSegmentExecutionResult?> ReconcileAsync(
        GenerationSegmentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateProvider(request.ProviderStableId);

        var key = ContentAddressedSegmentKey.Create(
            request.CompiledPayload.Span,
            request.ProviderStableId,
            request.OperationStableId,
            request.VoiceStableId,
            request.CompilationProfileId);

        var response = await _provider.ReconcileAsync(request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        if (response.Disposition == SubmissionDisposition.Accepted && !response.MediaBytes.IsEmpty)
        {
            await _cache.StoreAsync(key, response.MediaBytes, cancellationToken).ConfigureAwait(false);
        }

        return FromProviderResponse(false, key, response);
    }

    private void ValidateProvider(string providerStableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerStableId);
        if (!string.Equals(_provider.ProviderStableId, providerStableId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Generation request provider does not match the bound provider instance.");
        }
    }

    private static GenerationSegmentExecutionResult FromProviderResponse(
        bool cacheHit,
        ContentAddressedSegmentKey key,
        GenerationProviderResponse response) =>
        new(
            cacheHit,
            response.Disposition,
            response.ProviderRequestId,
            response.MediaBytes,
            response.DiagnosticCode,
            response.RetryAfter,
            key);
}
