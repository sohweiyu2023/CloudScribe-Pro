using CloudScribe.Application.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Application.Logging;

public static partial class CloudScribeLog
{
    [LoggerMessage(EventId = 1623, Level = LogLevel.Warning, Message = "Long Audio summary unavailable: root {RootAttemptId}, stage {Stage}, numeric local error {LocalHResult}. Saved audio and financial records are unchanged; no total duration or billing claim is made.")]
    public static partial void LongAudioRunSummaryUnavailable(ILogger logger, Guid rootAttemptId, LongAudioWorkflowStage stage, int localHResult);

    [LoggerMessage(EventId = 1622, Level = LogLevel.Information, Message = "Long Audio run summary v1: root {RootAttemptId}, stage {Stage}, elapsed milliseconds {ElapsedMilliseconds}, recorded active milliseconds {RecordedActiveMilliseconds}, earlier timing unknown {EarlierHistoryUnknown}, active timing incomplete {ActiveTimeIncomplete}, retained attempts including failures/replacements {RetainedAttempts}, job retained commitment USD cents {JobCommitmentUsdCents}, all-job retained commitments USD cents {AllCommitmentsUsdCents}, cumulative provision USD cents {ProvisionUsdCents}, approved cap USD cents {CapUsdCents}. Holds and provision are NOT actual charges. Actual provider billing is unavailable. Elapsed includes pauses; active is locally measured run time including waits. No text, voice, credentials or output paths retained.")]
    public static partial void LongAudioRunSummary(ILogger logger, Guid rootAttemptId, LongAudioWorkflowStage stage,
        long elapsedMilliseconds, long recordedActiveMilliseconds, bool earlierHistoryUnknown, bool activeTimeIncomplete,
        int retainedAttempts, long jobCommitmentUsdCents, long allCommitmentsUsdCents, long provisionUsdCents, long capUsdCents);
}
