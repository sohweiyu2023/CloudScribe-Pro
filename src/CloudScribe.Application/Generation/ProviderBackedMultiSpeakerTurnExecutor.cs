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

        var response = await provider.SubmitAsync(request, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Multi-speaker provider returned no response.");
        if (string.IsNullOrWhiteSpace(response.DiagnosticCode))
            throw new InvalidOperationException("Multi-speaker provider responses require an explicit diagnostic code.");

        return response.Disposition switch
        {
            SubmissionDisposition.Accepted => new(turn.TurnIndex, true, false, response.DiagnosticCode),
            SubmissionDisposition.UnknownRequiresReconciliation => new(turn.TurnIndex, false, true, response.DiagnosticCode),
            SubmissionDisposition.RejectedSafeToRetry => new(turn.TurnIndex, false, false, response.DiagnosticCode),
            SubmissionDisposition.NotSubmitted => new(turn.TurnIndex, false, false, response.DiagnosticCode),
            _ => throw new InvalidOperationException("Unsupported provider submission disposition for multi-speaker execution.")
        };
    }

    private static void ValidateRouteIdentity(string providerStableId, string voiceStableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceStableId);
    }
}
