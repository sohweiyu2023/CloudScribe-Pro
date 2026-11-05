using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private async Task RequireCaptureResumeReadyAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, CurrentFinancial financial, ChirpWholeArticleJob job, CancellationToken token)
    {
        if (policy.SentenceRecovery is null || job.State != ChirpWholeArticleState.Stopped || job.ActiveOrdinal is null
            || !string.Equals(job.ReasonCode, "article.private-capture-required", StringComparison.Ordinal)
            || job.MaximumRecoveryRequests < 2 || job.ExpiresAtUtc != policy.ExpiresAtUtc
            || clock.GetUtcNow() < job.LastObservedAtUtc || ChirpPolicyValidation.IsJobExpired(job, clock.GetUtcNow())
            || financial.Attempts.Any(x => !Equals(x[16], 1L)) || financial.Old.QueueStates.Any(x => x.Unresolved != 0))
            throw new InvalidOperationException("Only a terminal capture stop in the same admitted family can receive fresh cost review.");
        Guid root = await RecoverableOrdinalAsync(connection, transaction, policy, job.PlanSha256, job.ActiveOrdinal.Value, token).ConfigureAwait(false);
        await RequireRecoveryReadyAsync(connection, transaction, policy, financial, root, token).ConfigureAwait(false);
    }
}
