using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    /// <summary>Read-only prerequisite for refreshing expired evidence before an entirely unreserved article request.</summary>
    public async Task RequireUnreservedCatalogRefreshAsync(ChirpWholeArticleJob expected, int ordinal,
        ChirpArticleRecoveryChild? child, CancellationToken token = default)
    {
        var policy = await PolicyAsync(token).ConfigureAwait(false);
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var job = await ReadWholeArticleJobAsync(connection, transaction, expected.JobId, token).ConfigureAwait(false);
        if (job.State != ChirpWholeArticleState.Executing || job.ActiveOrdinal != ordinal
            || !string.Equals(ChirpPolicyValidation.Hash(job), ChirpPolicyValidation.Hash(expected), StringComparison.Ordinal)
            || !string.Equals(job.PolicySha256, ChirpPolicyValidation.Hash(policy), StringComparison.Ordinal))
            throw new InvalidOperationException("Catalog refresh requires the exact current executing article checkpoint.");
        RequireWholeJobLifetime(job);
        var financial = await ReadFinancialAsync(connection, transaction, policy, token).ConfigureAwait(false);
        if (financial.Attempts.Any(x => Equals(x[16], 0L)) || financial.Old.QueueStates.Any(x => x.Unresolved != 0))
            throw new InvalidOperationException("Existing reserved or uncertain requests must be reconciled before a preparation refresh.");
        var evidence = await _evidence.ResolveAsync(connection, transaction, policy, job.DocumentId, financial.Old, token).ConfigureAwait(false);
        if (!string.Equals(evidence.Article.Proposal.PlanSha256, job.PlanSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The current article source or plan changed before catalog refresh.");
        string key;
        if (child is null)
        {
            if (financial.Attempts.Any(x => Equals(x[1], job.PlanSha256) && Equals(x[2], (long)ordinal)))
                throw new InvalidOperationException("An existing root request cannot be refreshed or retried automatically.");
            key = Key(job.PlanSha256, ordinal);
        }
        else
        {
            RequireRecoveryChildJob(job, child);
            if (financial.Attempts.Any(x => Equals(x[0], child.AuthorizationId.ToString("N")))
                || await ReadRecoveryChildAsync(connection, transaction, child.AuthorizationId, token).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("An existing child reservation cannot be refreshed or retried automatically.");
            key = RecoveryKey(child);
        }
        if (financial.Old.QueueStates.Any(x => string.Equals(x.Account, policy.AccountId, StringComparison.Ordinal)
            && string.Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal)
            && string.Equals(x.Key, key, StringComparison.Ordinal)))
            throw new InvalidOperationException("The target queue identity already exists; no preparation refresh is permitted.");
        await RequirePolicyCurrentAsync(policy, token).ConfigureAwait(false);
    }
}
