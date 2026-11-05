using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private static async Task<ChirpWholeArticleJob?> FindReviewedWholeJobAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, string plan, CancellationToken token)
    {
        var current = await FindWholeArticleJobAsync(connection, transaction, ChirpPolicyValidation.Hash(policy), plan, token).ConfigureAwait(false);
        var id = policy.RecoveryContinuation?.JobId ?? policy.CompletionContinuity?.JobId;
        if (current is not null || id is null) return current;
        var previous = await ReadWholeArticleJobAsync(connection, transaction, id.Value, token).ConfigureAwait(false);
        return string.Equals(previous.PlanSha256, plan, StringComparison.Ordinal) ? previous : null;
    }

    private async Task RequireReviewedContinuationAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, CurrentFinancial financial, ChirpWholeArticleJob job, CancellationToken token)
    {
        if (policy.CompletionContinuity is not null || job.CompletionBound)
        {
            await RequireCompletionCheckpointAsync(connection, transaction, policy, financial, job, token).ConfigureAwait(false);
            return;
        }
        if (string.Equals(job.PolicySha256, ChirpPolicyValidation.Hash(policy), StringComparison.Ordinal))
        {
            await RequireCaptureResumeReadyAsync(connection, transaction, policy, financial, job, token).ConfigureAwait(false);
            return;
        }
        var c = policy.RecoveryContinuation ?? throw new InvalidOperationException("The stopped family requires explicit reviewed continuation.");
        if (job.JobId != c.JobId || job.State != ChirpWholeArticleState.Stopped || job.ActiveOrdinal is null
            || job.ReasonCode is not ("article.guard-rejected-reconcile" or "article.private-capture-required")
            || !string.Equals(ChirpPolicyValidation.Hash(job), c.PreviousJobSha256, StringComparison.Ordinal)
            || !string.Equals(job.PolicySha256, ChirpPolicyValidation.Hash(c.PreviousPolicy), StringComparison.Ordinal)
            || job.ExpiresAtUtc != policy.ExpiresAtUtc || clock.GetUtcNow() < job.LastObservedAtUtc || ChirpPolicyValidation.IsJobExpired(job, clock.GetUtcNow())
            || financial.Attempts.Any(x => !Equals(x[16], 1L)) || financial.Old.QueueStates.Any(x => x.Unresolved != 0))
            throw new InvalidOperationException("The exact stopped recovery checkpoint or terminal financial evidence changed.");
        var failed = await ReadRecoveryChildAsync(connection, transaction, c.FailedAuthorizationId, token).ConfigureAwait(false);
        Guid root = await RecoverableOrdinalAsync(connection, transaction, policy, job.PlanSha256, job.ActiveOrdinal.Value, token).ConfigureAwait(false);
        if (failed is null ? root != c.FailedAuthorizationId : failed.JobId != job.JobId || failed.CoverageOrdinal != job.ActiveOrdinal
            || failed.RootAuthorizationId != root || failed.Depth >= policy.SentenceRecovery!.MaximumDepth)
            throw new InvalidOperationException("The reviewed root or child cannot authorize another bounded subdivision.");
        var row = financial.Attempts.Single(x => Equals(x[0], c.FailedAuthorizationId.ToString("N")));
        await RequireSentenceRejectionAsync(connection, transaction, policy, row, token).ConfigureAwait(false);
        await RequireRecoveryReadyAsync(connection, transaction, policy, financial, root, token).ConfigureAwait(false);
    }

    private async Task<ChirpWholeArticleJob> ContinueReviewedWholeJobAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, CurrentFinancial financial, ChirpWholeArticleJob previous, ChirpWholeArticleReview review, CancellationToken token)
    {
        await RequireReviewedContinuationAsync(connection, transaction, policy, financial, previous, token).ConfigureAwait(false);
        bool samePolicy = string.Equals(previous.PolicySha256, ChirpPolicyValidation.Hash(policy), StringComparison.Ordinal);
        var next = previous with
        {
            PolicySha256 = ChirpPolicyValidation.Hash(policy),
            ReviewSha256 = review.ReviewSha256,
            State = ChirpWholeArticleState.Executing,
            ReasonCode = samePolicy ? "article.reviewed-capture-resume" : "article.reviewed-build-continuation",
            LastObservedAtUtc = clock.GetUtcNow()
        };
        if (policy.CompletionContinuity is not null || previous.CompletionBound)
            next = next with { CompletionBound = true, State = ChirpWholeArticleState.Ready, ActiveOrdinal = null, ReasonCode = "article.completion-bound-continuity" };
        if (!samePolicy && policy.CompletionContinuity is not null)
            next = next with
            {
                MaximumAdditionalMinorUnits = policy.SentenceRecovery is not null ? policy.Budget.ProjectCeilingMinorUnits : previous.MaximumAdditionalMinorUnits,
                MaximumRecoveryMinorUnits = policy.SentenceRecovery?.MaximumAdditionalMinorUnits ?? 0,
                MaximumRecoveryRequests = policy.SentenceRecovery?.MaximumChildAttemptsPerJob ?? 0
            };
        if (!samePolicy && policy.RecoveryLimitRevision is not null)
        {
            long increase = policy.SentenceRecovery!.MaximumAdditionalMinorUnits
                - policy.RecoveryContinuation!.PreviousPolicy.SentenceRecovery!.MaximumAdditionalMinorUnits;
            next = next with
            {
                MaximumAdditionalMinorUnits = checked(previous.MaximumAdditionalMinorUnits + increase),
                MaximumRecoveryMinorUnits = checked(previous.MaximumRecoveryMinorUnits + increase),
                MaximumRecoveryRequests = checked((int)(previous.MaximumRecoveryMinorUnits + increase))
            };
        }
        using var update = ChirpArticleSql.Command(connection, transaction,
            "UPDATE chirp_whole_article_jobs SET PolicySha256=$new WHERE JobId=$id AND PolicySha256=$old AND JobSha256=$head;",
            ("$new", next.PolicySha256), ("$id", next.JobId.ToString("N")), ("$old", previous.PolicySha256), ("$head", ChirpPolicyValidation.Hash(previous)));
        if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidOperationException("The reviewed checkpoint changed concurrently.");
        await WriteWholeArticleJobAsync(connection, transaction, next, false, token).ConfigureAwait(false);
        await RequirePolicyCurrentAsync(policy, token).ConfigureAwait(false);
        transaction.Commit();
        return next;
    }

    private static async Task<bool> HasWholeJobPolicyHistoryAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpWholeArticleJob job, string policy, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT JobJson,JobSha256 FROM chirp_whole_article_events WHERE JobId=$id ORDER BY Sequence LIMIT 10001;", ("$id", job.JobId.ToString("N")));
        using var rows = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        int count = 0; bool found = false;
        while (await rows.ReadAsync(token).ConfigureAwait(false))
        {
            if (++count > 10_000 || rows.GetString(0).Length > 8192) throw new InvalidDataException("Recovery policy history exceeds its bound.");
            var prior = JsonSerializer.Deserialize<ChirpWholeArticleJob>(rows.GetString(0)) ?? throw new InvalidDataException("Recovery history is invalid.");
            if (prior.JobId != job.JobId || !string.Equals(prior.PlanSha256, job.PlanSha256, StringComparison.Ordinal)
                || prior.ExpiresAtUtc != job.ExpiresAtUtc || !string.Equals(ChirpPolicyValidation.Hash(prior), rows.GetString(1), StringComparison.Ordinal))
                throw new InvalidDataException("Recovery policy history changed.");
            found |= string.Equals(prior.PolicySha256, policy, StringComparison.Ordinal);
        }
        return found;
    }
}
