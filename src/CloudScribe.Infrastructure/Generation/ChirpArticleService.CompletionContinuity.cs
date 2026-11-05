using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private void RequireWholeJobLifetime(ChirpWholeArticleJob job)
    {
        if (ChirpPolicyValidation.IsJobExpired(job, clock.GetUtcNow()))
            throw CloudScribe.Application.Logging.LocalRequestDiagnostics.Mark(new InvalidOperationException("The local article authorization ended before another request."),
                CloudScribe.Application.Logging.LocalRequestReason.ReviewExpired, admissionExpiresUtc: job.ExpiresAtUtc);
    }

    private async Task RequireCompletionCheckpointAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, CurrentFinancial financial, ChirpWholeArticleJob job, CancellationToken token)
    {
        if (clock.GetUtcNow() < job.LastObservedAtUtc || financial.Attempts.Any(x => !Equals(x[16], 1L))
            || financial.Old.QueueStates.Any(x => x.Unresolved != 0))
            throw new InvalidOperationException("Completion continuity cannot erase a pending, uncertain or future-clock checkpoint.");
        bool same = string.Equals(job.PolicySha256, ChirpPolicyValidation.Hash(policy), StringComparison.Ordinal);
        if (!same)
        {
            var c = policy.CompletionContinuity ?? throw new InvalidOperationException("Explicit completion-bound continuity is required.");
            if (job.JobId != c.JobId || !string.Equals(ChirpPolicyValidation.Hash(job), c.PreviousJobSha256, StringComparison.Ordinal)
                || !string.Equals(job.PolicySha256, ChirpPolicyValidation.Hash(c.PreviousPolicy), StringComparison.Ordinal)
                || job.ExpiresAtUtc != policy.ExpiresAtUtc)
                throw new InvalidOperationException("The exact completion checkpoint or immutable original grouping changed.");
        }
        else if (!job.CompletionBound || !ChirpPolicyValidation.IsCompletionBound(policy))
            throw new InvalidOperationException("An ordinary expired job cannot infer completion-bound authority.");
        if (job.ActiveOrdinal is { } ordinal)
        {
            var progress = await ProgressAsync(job.DocumentId, token).ConfigureAwait(false);
            var part = progress.Parts.Single(x => x.CoverageOrdinal == ordinal);
            if (!IsAccepted(part) && !string.Equals(part.Status, "Not requested", StringComparison.Ordinal))
                _ = await RecoverableOrdinalAsync(connection, transaction, policy, job.PlanSha256, ordinal, token).ConfigureAwait(false);
        }
    }

    private async Task RequireCompletionPredecessorAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, CurrentFinancial financial, CancellationToken token)
    {
        if (policy.CompletionContinuity is not { } c) return;
        var job = await ReadWholeArticleJobAsync(connection, transaction, c.JobId, token).ConfigureAwait(false);
        if (string.Equals(job.PolicySha256, ChirpPolicyValidation.Hash(policy), StringComparison.Ordinal)) return;
        await RequireCompletionCheckpointAsync(connection, transaction, policy, financial, job, token).ConfigureAwait(false);
    }
}
