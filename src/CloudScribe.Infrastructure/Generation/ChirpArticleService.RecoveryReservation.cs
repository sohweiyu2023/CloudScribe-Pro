using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private async Task InsertRecoveryChildAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, ChirpArticleRecoveryChild child, ChirpRequestIntent intent,
        CurrentFinancial financial, CancellationToken token)
    {
        var grant = policy.SentenceRecovery ?? throw new InvalidOperationException("No bounded recovery authority exists.");
        var parent = financial.Attempts.Single(x => Equals(x[0], child.ParentAuthorizationId.ToString("N")));
        var expected = await DeriveRecoveryChildrenAsync(connection, transaction, policy, child.JobId, parent, token).ConfigureAwait(false);
        if (expected.Count(x => x == child) != 1) throw new InvalidOperationException("The requested child differs from the exact natural binary plan.");
        var queue = financial.Old.Queue.Single(x => Equals(x[0], policy.AccountId)
            && Equals(x[1], GoogleGenerationProvider.SynthesizeOperationStableId) && Equals(x[2], parent[7]));
        string id = child.AuthorizationId.ToString("N"), key = SuccessorKey(child.PlanSha256, child.CoverageOrdinal, child.AuthorizationId);
        using (var initialize = ChirpArticleSql.Command(connection, transaction, """
            INSERT INTO google_generation_queue_states(AccountId,OperationStableId,IdempotencyKey,UnresolvedSubmission,ProviderRequestId,SubmissionAttempted)
            VALUES($account,$operation,$key,0,NULL,0);
            """, ("$account", policy.AccountId), ("$operation", GoogleGenerationProvider.SynthesizeOperationStableId), ("$key", key)))
            await initialize.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        using (var insert = ChirpArticleSql.Command(connection, transaction, """
            INSERT INTO chirp_article_split_attempts VALUES($id,$plan,$ordinal,$policy,$approval,$account,$project,$key,$review,$envelope,
                $currency,$scale,$estimate,$reserved,'',$now,0,NULL,$root,$parent,$state,$grant,$grantId);
            """, ("$id", id), ("$plan", child.PlanSha256), ("$ordinal", child.CoverageOrdinal), ("$policy", ChirpPolicyValidation.Hash(policy)),
            ("$approval", policy.SourceApprovalSha256), ("$account", policy.AccountId), ("$project", policy.ProjectId), ("$key", key),
            ("$review", ChirpPolicyValidation.Hash(child)), ("$envelope", JsonSerializer.Serialize(intent.Envelope with { ChirpAuthorizationId = child.AuthorizationId })),
            ("$currency", policy.Currency), ("$scale", policy.Scale), ("$estimate", intent.EstimateMinorUnits), ("$reserved", Math.Max(1, intent.EstimateMinorUnits)),
            ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()), ("$root", child.RootAuthorizationId.ToString("N")),
            ("$parent", child.ParentAuthorizationId.ToString("N")), ("$state", ChirpPolicyValidation.Hash(queue)),
            ("$grant", ChirpPolicyValidation.Hash(grant)), ("$grantId", grant.GrantId.ToString("N"))))
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        using var mapping = ChirpArticleSql.Command(connection, transaction, """
            INSERT INTO chirp_article_split_mappings VALUES($id,$parent,$slot,$job,$json,$hash);
            """, ("$id", id), ("$parent", child.ParentAuthorizationId.ToString("N")), ("$slot", child.ChildSlot),
            ("$job", child.JobId.ToString("N")), ("$json", JsonSerializer.Serialize(child)), ("$hash", ChirpPolicyValidation.Hash(child)));
        await mapping.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async Task RequireRecoveryReadyAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, CurrentFinancial financial, Guid root, CancellationToken token, Guid? revalidatingJob = null)
    {
        foreach (var attempt in financial.Attempts)
        {
            if (Equals(attempt[16], 0L))
            {
                var held = await ReadRecoveryChildAsync(connection, transaction, Guid.ParseExact((string)attempt[0]!, "N"), token).ConfigureAwait(false);
                if (revalidatingJob is null || held is null || held.JobId != revalidatingJob || held.RootAuthorizationId != root)
                    throw new InvalidOperationException("Revalidate the existing held child before another reservation.");
            }
            var queue = financial.Old.QueueStates.SingleOrDefault(x => Equals(x.Account, attempt[5]) && Equals(x.Key, attempt[7])
                && Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId));
            if (queue is null || queue.Unresolved != 0) throw new InvalidOperationException("An uncertain request prevents recovery.");
        }
        foreach (var attempt in financial.Extra.Where(x => Equals(x[16], 1L)))
        {
            var queue = financial.Old.QueueStates.Single(x => Equals(x.Account, attempt[5]) && Equals(x.Key, attempt[7])
                && Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId));
            if (string.Equals(queue.DiagnosticCode, "google-accepted", StringComparison.Ordinal))
            {
                var journal = await ReadJournalAsync(connection, transaction, (string)attempt[0]!, token).ConfigureAwait(false);
                await VerifyMediaAsync(journal.Receipt.Audio.AudioPath, journal.Receipt.Audio, token).ConfigureAwait(false);
            }
            else
            {
                var child = await ReadRecoveryChildAsync(connection, transaction, Guid.ParseExact((string)attempt[0]!, "N"), token).ConfigureAwait(false);
                if (!Equals(attempt[0], root.ToString("N")) && child?.RootAuthorizationId != root)
                {
                    if (await IsCompletedRecoveryFailureAsync(connection, transaction, policy, attempt, token).ConfigureAwait(false)) continue;
                    throw new InvalidOperationException("A different failed range must be reconciled before recovery.");
                }
                await RequireSentenceRejectionAsync(connection, transaction, policy, attempt, token).ConfigureAwait(false);
            }
        }
    }
}
