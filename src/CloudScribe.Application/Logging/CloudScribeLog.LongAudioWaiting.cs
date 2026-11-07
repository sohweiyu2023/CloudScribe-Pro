using Microsoft.Extensions.Logging;

namespace CloudScribe.Application.Logging;

public static partial class CloudScribeLog
{
    [LoggerMessage(EventId = 1625, Level = LogLevel.Warning, Message = "Long Audio status check for attempt {AttemptId} received no answer within {DeadlineSeconds} s. Verified known-operation GET continuation ready: {CanContinuePolling}. Missing required evidence remains a stop; no synthesis was resubmitted.")]
    public static partial void LongAudioPollDeadline(ILogger logger, Guid attemptId, long deadlineSeconds, bool canContinuePolling);

    [LoggerMessage(EventId = 1626, Level = LogLevel.Warning, Message = "Long Audio run for root {RootAttemptId} stopped waiting at the user's request during stage {Stage}. A request already sent may still finish and be charged; its saved outcome is checked before any further request.")]
    public static partial void LongAudioUserStoppedWaiting(ILogger logger, Guid rootAttemptId, CloudScribe.Application.Generation.LongAudioWorkflowStage stage);
}
