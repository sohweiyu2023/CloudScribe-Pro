using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage7MultiSpeakerTurnExecutionCoordinatorTests
{
    [Fact]
    public async Task Ambiguous_turn_stops_later_turns_from_executing()
    {
        var executor = new RecordingExecutor(reconcileAtTurn: 1);
        var coordinator = new MultiSpeakerTurnExecutionCoordinator(executor);
        var turns = new[]
        {
            new MultiSpeakerTurn(0, "a", "first"),
            new MultiSpeakerTurn(1, "a", "second"),
            new MultiSpeakerTurn(2, "a", "third"),
        };
        var routes = new[] { new SpeakerRoute("a", "provider", "voice", false) };
        var health = new Dictionary<string, bool> { ["provider"] = true };

        var outcomes = await coordinator.ExecuteAsync(turns, routes, health, explicitFallbackAllowed: false);

        Assert.Equal(2, outcomes.Count);
        Assert.True(outcomes[1].RequiresReconciliation);
        Assert.Equal(new[] { 0, 1 }, executor.ExecutedTurns);
    }

    [Fact]
    public async Task Contradictory_success_and_reconciliation_outcome_fails_closed()
    {
        var coordinator = new MultiSpeakerTurnExecutionCoordinator(new ContradictoryExecutor());
        var turns = new[] { new MultiSpeakerTurn(0, "a", "first") };
        var routes = new[] { new SpeakerRoute("a", "provider", "voice", false) };
        var health = new Dictionary<string, bool> { ["provider"] = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteAsync(turns, routes, health, explicitFallbackAllowed: false));
    }

    private sealed class RecordingExecutor : IMultiSpeakerTurnExecutor
    {
        private readonly int _reconcileAtTurn;
        public List<int> ExecutedTurns { get; } = [];

        public RecordingExecutor(int reconcileAtTurn) => _reconcileAtTurn = reconcileAtTurn;

        public Task<MultiSpeakerTurnExecutionOutcome> ExecuteAsync(
            PlannedSpeakerTurn turn,
            CancellationToken cancellationToken = default)
        {
            ExecutedTurns.Add(turn.TurnIndex);
            var reconcile = turn.TurnIndex == _reconcileAtTurn;
            return Task.FromResult(new MultiSpeakerTurnExecutionOutcome(
                turn.TurnIndex,
                Succeeded: !reconcile,
                RequiresReconciliation: reconcile,
                DiagnosticCode: reconcile ? "submission-ambiguous" : "ok"));
        }
    }

    private sealed class ContradictoryExecutor : IMultiSpeakerTurnExecutor
    {
        public Task<MultiSpeakerTurnExecutionOutcome> ExecuteAsync(
            PlannedSpeakerTurn turn,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MultiSpeakerTurnExecutionOutcome(
                turn.TurnIndex,
                Succeeded: true,
                RequiresReconciliation: true,
                DiagnosticCode: "invalid-contradiction"));
    }
}
