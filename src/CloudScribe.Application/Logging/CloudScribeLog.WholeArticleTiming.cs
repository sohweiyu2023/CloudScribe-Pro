using Microsoft.Extensions.Logging;

namespace CloudScribe.Application.Logging;

public static partial class CloudScribeLog
{
    [LoggerMessage(EventId = 1624, Level = LogLevel.Information, Message = "Standard whole-article run timing v1: job {JobId} (empty means start unacknowledged), this-run elapsed milliseconds {ElapsedMilliseconds}, verified complete {Completed}, job start attempted {StartAttempted}. Includes provider waits and local processing. Earlier runs and pauses are excluded; whole-job duration and actual charges are not inferred. No text, credentials, voice or output paths retained.")]
    public static partial void WholeArticleRunTiming(ILogger logger, Guid jobId, long elapsedMilliseconds, bool completed, bool startAttempted);
}
