using CloudScribe.Application.Batch;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>Maps a finished or stopped complete-article job to a batch outcome. Anything that might hide a submitted
/// request is Uncertain, so the batch never resends it; only a definite provider rejection is Failed.</summary>
public static class BatchArticleOutcomes
{
    public static BatchNarrationOutcome FromJob(ChirpWholeArticleJob job, long approvedMaximumMinorUnits, bool cancellationRequested)
    {
        ArgumentNullException.ThrowIfNull(job);
        long held = Math.Max(job.MaximumAdditionalMinorUnits, approvedMaximumMinorUnits);
        return job.State switch
        {
            ChirpWholeArticleState.Complete when job.Output is { } output =>
                BatchNarrationOutcome.Completed(output.Path, output.Bytes, output.Sha256, approvedMaximumMinorUnits, job.MaximumAdditionalMinorUnits),
            ChirpWholeArticleState.Stopped when string.Equals(job.ReasonCode, "article.cancelled-reconcile", StringComparison.Ordinal) && cancellationRequested =>
                new(BatchOutcomeKind.Paused, "batch.paused", EstimatedMaximumMinorUnits: approvedMaximumMinorUnits, ReservedMinorUnits: held),
            ChirpWholeArticleState.Stopped when string.Equals(job.ReasonCode, "article.provider-rejected-no-retry", StringComparison.Ordinal) =>
                new(BatchOutcomeKind.Failed, job.ReasonCode, EstimatedMaximumMinorUnits: approvedMaximumMinorUnits, ReservedMinorUnits: held),
            _ => new(BatchOutcomeKind.Uncertain, Safe(job.ReasonCode), EstimatedMaximumMinorUnits: approvedMaximumMinorUnits, ReservedMinorUnits: held),
        };
    }

    /// <summary>Only fixed reason codes are kept; anything unexpected becomes a generic reconcile code.</summary>
    private static string Safe(string? reason) =>
        reason is { Length: > 0 and <= 80 } && reason.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ? reason : "article.stopped-reconcile";
}
