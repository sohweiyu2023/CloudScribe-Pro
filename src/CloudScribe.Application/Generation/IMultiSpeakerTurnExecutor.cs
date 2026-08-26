using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public interface IMultiSpeakerTurnExecutor
{
    Task<MultiSpeakerTurnExecutionOutcome> ExecuteAsync(
        PlannedSpeakerTurn turn,
        CancellationToken cancellationToken = default);
}
