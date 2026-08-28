using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Generation;

public sealed class ProviderBackedMultiSpeakerTurnExecutor : IMultiSpeakerTurnExecutor
{
    private readonly Func<string, IGenerationProvider> _providerResolver;
    private readonly Func<PlannedSpeakerTurn, GenerationProviderRequest> _requestFactory;

    public ProviderBackedMultiSpeakerTurnExecutor(
        Func<string, IGenerationProvider> providerResolver,
        Func<PlannedSpeakerTurn, GenerationProviderRequest> requestFactory)
    {
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
    }

    public async Task<MultiSpeakerTurnExecutionOutcome> ExecuteAsync(
        PlannedSpeakerTurn turn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ValidateRouteIdentity(turn.Route.ProviderStableId, turn.Route.VoiceStableId);

        var provider = _providerResolver(turn.Route.ProviderStableId)
            ?? throw new InvalidOperationException("No provider is registered for the selected speaker route.");
        if (!string.Equals(provider.ProviderStableId, turn.Route.ProviderStableId, StringComparison.Ordinal))
            throw new InvalidOperationException("Resolved provider identity does not match the planned speaker route.");

        var request = _requestFactory(turn)
            ?? throw new InvalidOperationException("Multi-speaker request factory returned no provider request.");
        if (!string.Equals(request.ProviderStableId, turn.Route.ProviderStableId, StringComparison.Ordinal))
            throw new InvalidOperationException("Provider request identity does not match the planned speaker route.");
        if (request.CompiledPayload.IsEmpty)
            throw new InvalidOperationException("Multi-speaker provider requests require a compiled payload.");

        var expectedMediaFormat = ParseExpectedMediaFormat(request.OutputFormat);
        var response = await provider.SubmitAsync(request, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Multi-speaker provider returned no response.");
        if (string.IsNullOrWhiteSpace(response.DiagnosticCode))
            throw new InvalidOperationException("Multi-speaker provider responses require an explicit diagnostic code.");

        return response.Disposition switch
        {
            SubmissionDisposition.Accepted => AcceptedOutcome(turn, response, expectedMediaFormat),
            SubmissionDisposition.UnknownRequiresReconciliation => new(turn.TurnIndex, false, true, response.DiagnosticCode),
            SubmissionDisposition.RejectedSafeToRetry => new(turn.TurnIndex, false, false, response.DiagnosticCode),
            SubmissionDisposition.NotSubmitted => new(turn.TurnIndex, false, false, response.DiagnosticCode),
            _ => throw new InvalidOperationException("Unsupported provider submission disposition for multi-speaker execution.")
        };
    }

    private static MultiSpeakerTurnExecutionOutcome AcceptedOutcome(
        PlannedSpeakerTurn turn,
        GenerationProviderResponse response,
        GenerationAudioFormat expectedMediaFormat)
    {
        var validation = ReturnedMediaValidator.Validate(response.MediaBytes.Span, response.MediaContentType);
        if (!validation.IsValid || validation.DetectedFormat != expectedMediaFormat)
            throw new InvalidDataException("Accepted multi-speaker provider media failed requested-format validation.");

        return new(turn.TurnIndex, true, false, response.DiagnosticCode);
    }

    private static GenerationAudioFormat ParseExpectedMediaFormat(string outputFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFormat);
        return outputFormat.Trim().ToLowerInvariant() switch
        {
            "wav" or "wave" or "audio/wav" or "audio/wave" => GenerationAudioFormat.Wav,
            "mp3" or "mpeg" or "audio/mpeg" => GenerationAudioFormat.Mp3,
            _ => throw new NotSupportedException($"Multi-speaker output format '{outputFormat}' is not supported.")
        };
    }

    private static void ValidateRouteIdentity(string providerStableId, string voiceStableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceStableId);
    }
}
