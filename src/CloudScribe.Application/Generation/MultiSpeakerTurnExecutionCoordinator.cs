namespace CloudScribe.Application.Generation;

public sealed record MultiSpeakerTurnExecutionOutcome(
    int TurnIndex,
    bool Succeeded,
    bool RequiresReconciliation,
    string DiagnosticCode);

public interface IMultiSpeakerTurnExecutor
{
    Task<MultiSpeakerTurnExecutionOutcome> ExecuteAsync(
        PlannedSpeakerTurn turn,
        CancellationToken cancellationToken = default);
}

public sealed class MultiSpeakerTurnExecutionCoordinator
{
    private readonly IMultiSpeakerTurnExecutor _executor;

    public MultiSpeakerTurnExecutionCoordinator(IMultiSpeakerTurnExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<IReadOnlyList<MultiSpeakerTurnExecutionOutcome>> ExecuteAsync(
        IReadOnlyList<MultiSpeakerTurn> turns,
        IReadOnlyCollection<SpeakerRoute> routes,
        IReadOnlyDictionary<string, bool> providerHealth,
        bool explicitFallbackAllowed,
        CancellationToken cancellationToken = default)
    {
        var planned = MultiSpeakerTurnPlanner.Plan(turns, routes, providerHealth, explicitFallbackAllowed);
        var outcomes = new List<MultiSpeakerTurnExecutionOutcome>(planned.Count);

        foreach (var turn in planned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await _executor.ExecuteAsync(turn, cancellationToken).ConfigureAwait(false);
            if (outcome.TurnIndex != turn.TurnIndex)
                throw new InvalidOperationException("Multi-speaker executor returned an outcome for the wrong turn.");
            if (string.IsNullOrWhiteSpace(outcome.DiagnosticCode))
                throw new InvalidOperationException("Multi-speaker execution outcomes require an explicit diagnostic code.");

            outcomes.Add(outcome);
            if (outcome.RequiresReconciliation)
                break;
            if (!outcome.Succeeded)
                break;
        }

        return outcomes;
    }
}
