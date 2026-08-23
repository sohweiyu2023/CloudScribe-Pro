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
    string OutputFormat,
    GenerationCacheTrustContext? CacheTrustContext = null,
    bool ForceFresh = false,
    CacheReuseMediaMetadata? ExpectedCacheMediaMetadata = null);

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
    private readonly IGenerationPrivateCacheKeyProvider? _privateCacheKeyProvider;

    public GenerationSegmentExecutor(
        IGenerationProvider provider,
        IGenerationSegmentCache cache,
        IGenerationPrivateCacheKeyProvider? privateCacheKeyProvider = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _privateCacheKeyProvider = privateCacheKeyProvider;
    }

    public async Task<GenerationSegmentExecutionResult> ExecuteAsync(
        GenerationSegmentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateProvider(request.ProviderStableId);
        var key = await CreatePrivateCacheKeyAsync(request, cancellationToken).ConfigureAwait(false);

        if (!request.ForceFresh)
        {
            var cached = await _cache.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (cached is { Length: > 0 } && IsEligibleCacheHit(request, cached))
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
        }

        var providerRequest = new GenerationProviderRequest(
            request.ProviderStableId,
            request.OperationStableId,
            request.AccountId,
            request.IdempotencyKey,
            request.CompiledPayload,
            request.OutputFormat);

        var response = await _provider.SubmitAsync(providerRequest, cancellationToken).ConfigureAwait(false);
        await ValidateAndCacheAcceptedMediaAsync(key, request.OutputFormat, response, cancellationToken).ConfigureAwait(false);
        return FromProviderResponse(false, key, response);
    }

    public async Task<GenerationSegmentExecutionResult?> ReconcileAsync(
        GenerationSegmentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateProvider(request.ProviderStableId);
        var key = await CreatePrivateCacheKeyAsync(request, cancellationToken).ConfigureAwait(false);

        var response = await _provider.ReconcileAsync(request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        await ValidateAndCacheAcceptedMediaAsync(key, request.OutputFormat, response, cancellationToken).ConfigureAwait(false);
        return FromProviderResponse(false, key, response);
    }

    public async ValueTask<ContentAddressedSegmentKey> CreatePrivateCacheKeyAsync(
        GenerationSegmentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var trust = request.CacheTrustContext
            ?? throw new InvalidOperationException("CloudScribe v2.23 requires an explicit complete cache trust context before cache lookup or publication.");
        ValidateTrustBinding(request, trust);
        var provider = _privateCacheKeyProvider
            ?? throw new InvalidOperationException("CloudScribe v2.23 requires an OS-protected private cache HMAC key provider; cache reuse is disabled until one is supplied.");

        using var keyMaterial = await provider.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var lookup = PrivateCacheLookupKey.Derive(keyMaterial.Span, trust, request.CompiledPayload.Span);
        return ContentAddressedSegmentKey.FromPrivateLookup(lookup);
    }

    private async Task ValidateAndCacheAcceptedMediaAsync(
        ContentAddressedSegmentKey key,
        string outputFormat,
        GenerationProviderResponse response,
        CancellationToken cancellationToken)
    {
        if (response.Disposition != SubmissionDisposition.Accepted)
        {
            return;
        }

        if (!IsExpectedValidMedia(response.MediaBytes.Span, response.MediaContentType, outputFormat))
        {
            throw new InvalidDataException("Provider returned accepted media that failed structural or requested-format validation; the bytes were not cached.");
        }

        await _cache.StoreAsync(key, response.MediaBytes, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsEligibleCacheHit(GenerationSegmentExecutionRequest request, ReadOnlySpan<byte> cachedMedia)
    {
        var expectedMetadata = request.ExpectedCacheMediaMetadata;
        if (expectedMetadata is null)
        {
            // v2.23 CACHE-002: container-only validation is not enough to prove a reusable hit.
            return false;
        }

        return CacheHitMediaEligibility.IsEligible(cachedMedia, request.OutputFormat, expectedMetadata);
    }

    private static bool IsExpectedValidMedia(ReadOnlySpan<byte> mediaBytes, string? contentType, string outputFormat)
    {
        var validation = ReturnedMediaValidator.Validate(mediaBytes, contentType);
        if (!validation.IsValid || validation.DetectedFormat is null)
        {
            return false;
        }

        var expected = outputFormat.Trim().ToLowerInvariant() switch
        {
            "wav" or "wave" => GenerationAudioFormat.Wav,
            "mp3" or "mpeg" => GenerationAudioFormat.Mp3,
            _ => throw new NotSupportedException($"Generation output format '{outputFormat}' is not supported by the strict returned-media validator."),
        };

        return validation.DetectedFormat.Value == expected;
    }

    private static void ValidateTrustBinding(GenerationSegmentExecutionRequest request, GenerationCacheTrustContext trust)
    {
        trust.Validate();
        if (!string.Equals(trust.ProviderStableId, request.ProviderStableId, StringComparison.Ordinal) ||
            !string.Equals(trust.AccountId, request.AccountId, StringComparison.Ordinal) ||
            !string.Equals(trust.OperationStableId, request.OperationStableId, StringComparison.Ordinal) ||
            !string.Equals(trust.VoiceStableId, request.VoiceStableId, StringComparison.Ordinal) ||
            !string.Equals(trust.OutputFormat, request.OutputFormat, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cache trust context does not match the immutable generation request binding.");
        }
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
