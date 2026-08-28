using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Generation;

public sealed class VoiceLabAuditionCoordinator
{
    private readonly Func<CancellationToken, Task<ReadOnlyMemory<byte>?>> _cacheReader;
    private readonly Func<CancellationToken, Task<GenerationProviderResponse>> _submitProvider;

    public VoiceLabAuditionCoordinator(
        Func<CancellationToken, Task<ReadOnlyMemory<byte>?>> cacheReader,
        Func<CancellationToken, Task<GenerationProviderResponse>> submitProvider)
    {
        _cacheReader = cacheReader ?? throw new ArgumentNullException(nameof(cacheReader));
        _submitProvider = submitProvider ?? throw new ArgumentNullException(nameof(submitProvider));
    }

    public Task<VoiceLabAuditionOutcome> ExecuteBoundAsync(
        VoiceLabAuditionRequest request,
        string providerStableId,
        string accountStableId,
        string projectStableId,
        string voiceStableId,
        string voiceFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        VoiceLabAuditionRequestBindingPolicy.RequireBoundSelection(
            request.Selection,
            providerStableId,
            accountStableId,
            projectStableId,
            voiceStableId,
            voiceFingerprint);

        return ExecuteAsync(request, cancellationToken);
    }

    public async Task<VoiceLabAuditionOutcome> ExecuteAsync(
        VoiceLabAuditionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Selection is null)
            throw new ArgumentException("Voice audition requests require a catalog selection.", nameof(request));
        request.Selection.Validate();
        if (string.IsNullOrWhiteSpace(request.OutputFormat))
            throw new ArgumentException("Voice audition requests require an output format.", nameof(request));

        ReadOnlyMemory<byte>? cached = null;
        var cacheHitEligible = false;
        if (request.CachePolicyEligible && !request.ForceFresh)
        {
            cached = await _cacheReader(cancellationToken).ConfigureAwait(false);
            cacheHitEligible = cached is { IsEmpty: false } && IsExpectedMedia(cached.Value.Span, request.OutputFormat);
        }

        var authorization = VoiceAuditionExecutionGate.Authorize(
            cacheHitEligible,
            request.ForceFresh,
            request.ExplicitSpendApproved,
            request.Selection.CapabilityCurrent,
            request.PricingCurrent);

        if (authorization.UseCachedMedia)
        {
            if (cached is not { IsEmpty: false })
                throw new InvalidOperationException("Voice audition authorized cache reuse without available validated media.");
            return new(true, cached.Value, authorization.Reason);
        }

        if (!authorization.SubmitProviderRequest)
            throw new InvalidOperationException("Voice audition authorization produced neither cache reuse nor provider submission.");

        var response = await _submitProvider(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice audition provider returned no response.");
        if (response.Disposition != SubmissionDisposition.Accepted)
            throw new InvalidOperationException($"Voice audition provider did not return accepted media: {response.DiagnosticCode}");
        if (!IsExpectedMedia(response.MediaBytes.Span, request.OutputFormat))
            throw new InvalidDataException("Voice audition provider media failed requested-format validation.");
        if (string.IsNullOrWhiteSpace(response.DiagnosticCode))
            throw new InvalidOperationException("Voice audition provider responses require an explicit diagnostic code.");

        return new(false, response.MediaBytes, response.DiagnosticCode);
    }

    private static bool IsExpectedMedia(ReadOnlySpan<byte> bytes, string outputFormat)
    {
        var validation = ReturnedMediaValidator.Validate(bytes, null);
        if (!validation.IsValid || validation.DetectedFormat is null) return false;
        var expected = outputFormat.Trim().ToLowerInvariant() switch
        {
            "wav" or "wave" => GenerationAudioFormat.Wav,
            "mp3" or "mpeg" => GenerationAudioFormat.Mp3,
            _ => throw new NotSupportedException($"Voice audition output format '{outputFormat}' is not supported.")
        };
        return validation.DetectedFormat.Value == expected;
    }
}
