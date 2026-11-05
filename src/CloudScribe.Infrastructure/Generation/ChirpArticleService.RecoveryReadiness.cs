using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private async Task RequireWholeReviewReadyAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, CurrentFinancial financial, ChirpWholeArticleJob? existing, CancellationToken token)
    {
        await RequireCompletionPredecessorAsync(connection, transaction, policy, financial, token).ConfigureAwait(false);
        if (existing is not null && policy.CompletionContinuity is not null
            && !string.Equals(existing.PolicySha256, ChirpPolicyValidation.Hash(policy), StringComparison.Ordinal))
        {
            await RequireCompletionCheckpointAsync(connection, transaction, policy, financial, existing, token).ConfigureAwait(false);
            return;
        }
        if (existing?.State == ChirpWholeArticleState.Stopped)
        {
            await RequireReviewedContinuationAsync(connection, transaction, policy, financial, existing, token).ConfigureAwait(false);
            return;
        }
        if (existing is { State: ChirpWholeArticleState.Executing, MaximumRecoveryRequests: >= 2, ActiveOrdinal: { } ordinal })
        {
            RequireRecoveryJob(policy, existing);
            Guid? root = null;
            try { root = await RecoverableOrdinalAsync(connection, transaction, policy, existing.PlanSha256, ordinal, token).ConfigureAwait(false); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { /* Ordinary readiness retains its rejection. */ }
            if (root is { } failed)
            {
                await RequireRecoveryReadyAsync(connection, transaction, policy, financial, failed, token, existing.JobId).ConfigureAwait(false);
                return;
            }
        }
        await RequireReadyAsync(connection, transaction, financial, null, token).ConfigureAwait(false);
    }

    private static async Task<Guid> RecoverableOrdinalAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, string plan, int ordinal, CancellationToken token)
    {
        if (policy.SentenceRecovery is null) throw new InvalidOperationException("No independently admitted recovery permission exists.");
        using var command = ChirpArticleSql.Command(connection, transaction, """
            SELECT AuthorizationId FROM chirp_article_owned_attempts WHERE PlanSha256=$plan AND CoverageOrdinal=$ordinal
            AND AuthorizationId NOT IN(SELECT AuthorizationId FROM chirp_article_split_attempts) LIMIT 3;
            """, ("$plan", plan), ("$ordinal", ordinal));
        List<string> ids = [];
        using (var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await row.ReadAsync(token).ConfigureAwait(false)) ids.Add(row.GetString(0));
        if (ids.Count != 1) throw new InvalidOperationException("The exact original failed range is absent or ambiguous.");
        var rows = await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_owned_attempts", token).ConfigureAwait(false);
        await RequireSentenceRejectionAsync(connection, transaction, policy, rows.Single(x => Equals(x[0], ids[0])), token).ConfigureAwait(false);
        return Guid.ParseExact(ids[0], "N");
    }

    private async Task<bool> IsCompletedRecoveryFailureAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, object?[] row, CancellationToken token)
    {
        if (policy.SentenceRecovery is null) return false;
        await RequireSentenceRejectionAsync(connection, transaction, policy, row, token).ConfigureAwait(false);
        var stored = await StoredAsync(connection, transaction, (string)row[1]!, token).ConfigureAwait(false);
        var recovered = await RecoveredCoverageAsync(connection, transaction, stored, token).ConfigureAwait(false);
        return recovered.Any(x => x.CoverageOrdinal == checked((int)(long)row[2]!));
    }

    private static (int Requests, long MinorUnits) RecoveryReviewAllowance(ChirpArticleAdmissionPolicy policy,
        CurrentFinancial financial, string plan, int ordinaryRequests, long ordinaryCost)
    {
        if (policy.SentenceRecovery is not { } grant) return (0, 0);
        var current = financial.SplitAttempts;
        int requests = Math.Min(grant.MaximumChildAttemptsPerJob - current.Count(x => Equals(x[1], plan)), Math.Min(grant.MaximumChildAttemptsTotal - current.Count,
            policy.MaximumNewAttempts - financial.Extra.Length - ordinaryRequests));
        long minor = Math.Min(grant.MaximumAdditionalMinorUnits - current.Sum(x => (long)x[13]!),
            Math.Min(policy.MaximumAdditionalReservationMinorUnits - financial.Additional - ordinaryCost,
                Math.Min(financial.AllocationRemaining - ordinaryCost, policy.Budget.ProjectCeilingMinorUnits - financial.ProjectCommitted - ordinaryCost)));
        return requests < 2 || minor < 2 ? (0, 0) : (requests, minor);
    }
}
