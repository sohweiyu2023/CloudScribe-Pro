using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed class GoogleAuthorizedGenerationExecutor
{
    private readonly GoogleGenerationProvider _provider;
    private readonly GoogleGenerationAccount _account;
    private readonly GoogleCapabilitySnapshot _capabilities;
    private readonly GoogleGenerationSpendAuthorization _authorization;
    private readonly string _pricingProvenanceId;
    private readonly int _requestRevision;
    private readonly string _currency;
    private readonly int _scale;
    private readonly long _currentEstimateMinorUnits;
    private readonly TimeProvider _timeProvider;

    public GoogleAuthorizedGenerationExecutor(
        GoogleGenerationProvider provider,
        GoogleGenerationAccount account,
        GoogleCapabilitySnapshot capabilities,
        GoogleGenerationSpendAuthorization authorization,
        string pricingProvenanceId,
        int requestRevision,
        string currency,
        int scale,
        long currentEstimateMinorUnits,
        TimeProvider? timeProvider = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _account = (account ?? throw new ArgumentNullException(nameof(account))).Validate();
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        ArgumentException.ThrowIfNullOrWhiteSpace(pricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        if (requestRevision < 0) throw new ArgumentOutOfRangeException(nameof(requestRevision));
        if (scale is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(scale));
        if (currentEstimateMinorUnits < 0) throw new ArgumentOutOfRangeException(nameof(currentEstimateMinorUnits));
        _pricingProvenanceId = pricingProvenanceId;
        _requestRevision = requestRevision;
        _currency = currency;
        _scale = scale;
        _currentEstimateMinorUnits = currentEstimateMinorUnits;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<GenerationProviderResponse> SubmitAsync(
        GenerationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.ProviderStableId, GoogleGenerationProvider.StableProviderId, StringComparison.Ordinal))
            throw new InvalidOperationException("Authorized Google execution received a request for another provider.");
        if (!string.Equals(request.OperationStableId, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal))
            throw new InvalidOperationException("Google synthesis authorization cannot be reused for another provider operation.");
        if (!string.Equals(request.AccountId, _account.AccountId, StringComparison.Ordinal))
            throw new InvalidOperationException("Authorized Google execution account identity changed after approval.");

        var envelope = _authorization.Envelope;
        EnsureOutputFormatMatchesEncoding(request.OutputFormat, envelope.AudioEncoding);
        envelope.EnsureStillAuthorized(
            _account,
            _capabilities,
            _pricingProvenanceId,
            _requestRevision,
            request.CompiledPayload.Span,
            _timeProvider.GetUtcNow());
        _authorization.EnsureStillAuthorized(envelope, _currency, _scale, _currentEstimateMinorUnits);

        cancellationToken.ThrowIfCancellationRequested();
        return await _provider.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureOutputFormatMatchesEncoding(string outputFormat, string audioEncoding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFormat);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioEncoding);
        var expected = audioEncoding.Trim().ToUpperInvariant() switch
        {
            "MP3" => "mp3",
            "LINEAR16" => "wav",
            "FLAC" => "flac",
            "OGG_OPUS" => "ogg",
            _ => throw new InvalidOperationException($"Unsupported authorized Google audio encoding: {audioEncoding}"),
        };
        if (!string.Equals(outputFormat.Trim(), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Provider-neutral output format changed after Google encoding approval.");
    }
}
