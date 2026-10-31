using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private static async Task AppendPlanAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, AdmittedChirpArticle article, CancellationToken token)
    {
        string proposal = JsonSerializer.Serialize(article.Proposal), lineage = JsonSerializer.Serialize(article.Lineage), reused = JsonSerializer.Serialize(article.ReusedAudio);
        using (var existing = ChirpArticleSql.Command(connection, transaction,
            "SELECT ProposalJson FROM chirp_article_plans WHERE lower(DocumentId)=$document LIMIT 3;", ("$document", article.Proposal.DocumentId.ToString("D"))))
        {
            List<ChirpArticlePlanProposal> plans = [];
            using (var reader = await existing.ExecuteReaderAsync(token).ConfigureAwait(false))
                while (await reader.ReadAsync(token).ConfigureAwait(false)) plans.Add(ParseStored<ChirpArticlePlanProposal>(reader.GetString(0)));
            if (!plans.Any(x => string.Equals(x.PlanSha256, article.Proposal.PlanSha256, StringComparison.Ordinal))) plans.Add(article.Proposal);
            _ = ChirpRefinedPlanSelection.RequireUniqueLeaf(plans);
        }
        using (var insert = ChirpArticleSql.Command(connection, transaction, """
            INSERT INTO chirp_article_plans VALUES($plan,$document,$revision,$account,$project,$proposal,$lineage,$reused)
            ON CONFLICT(PlanSha256) DO NOTHING;
            """, ("$plan", article.Proposal.PlanSha256), ("$document", article.Proposal.DocumentId.ToString("D")),
            ("$revision", article.Proposal.RevisionId.ToString("D")), ("$account", policy.AccountId), ("$project", policy.ProjectId),
            ("$proposal", proposal), ("$lineage", lineage), ("$reused", reused)))
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        using var select = ChirpArticleSql.Command(connection, transaction,
            "SELECT AccountId,ProjectId,ProposalJson,LineageJson,ReusedAudioJson FROM chirp_article_plans WHERE PlanSha256=$plan;", ("$plan", article.Proposal.PlanSha256));
        using var row = await select.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await row.ReadAsync(token).ConfigureAwait(false) || !string.Equals(row.GetString(0), policy.AccountId, StringComparison.Ordinal)
            || !string.Equals(row.GetString(1), policy.ProjectId, StringComparison.Ordinal) || !string.Equals(row.GetString(2), proposal, StringComparison.Ordinal)
            || !string.Equals(row.GetString(3), lineage, StringComparison.Ordinal) || !string.Equals(row.GetString(4), reused, StringComparison.Ordinal))
            throw new InvalidOperationException("An immutable article plan already exists with different source, account, lineage or retained audio.");
    }

    private async Task AppendAttemptAsync(SqliteConnection connection, SqliteTransaction transaction, ChirpArticleAdmissionPolicy policy,
        Pending pending, ChirpApprovedAttempt approved, CancellationToken token)
    {
        var intent = pending.Intent;
        using (var queue = ChirpArticleSql.Command(connection, transaction, """
            INSERT INTO google_generation_queue_states(AccountId,OperationStableId,IdempotencyKey,UnresolvedSubmission,ProviderRequestId,SubmissionAttempted)
            VALUES($account,$operation,$key,0,NULL,0);
            """, ("$account", policy.AccountId), ("$operation", GoogleGenerationProvider.SynthesizeOperationStableId), ("$key", approved.IdempotencyKey)))
            await queue.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var grant = policy.SuccessorGrant;
        string table = grant is null ? "chirp_article_attempts" : "chirp_article_successor_attempts";
        string extraColumns = grant is null ? string.Empty : ",RootAuthorizationId,ParentAuthorizationId,ParentStateSha256,GrantSha256,GrantId";
        string extraValues = grant is null ? string.Empty : ",$root,$parent,$parentState,$grant,$grantId";
        string sql = "INSERT INTO " + table + "(" + """
            AuthorizationId,PlanSha256,CoverageOrdinal,PolicySha256,SourceApprovalSha256,AccountId,ProjectId,
            ChildKey,ReviewSha256,EnvelopeJson,Currency,Scale,EstimateMinorUnits,ReservedMinorUnits,ApprovedFinancialSha256,ApprovedAtUnixMilliseconds)
            """;
        sql = sql.TrimEnd().TrimEnd(')') + extraColumns + ") VALUES($id,$plan,$ordinal,$policy,$source,$account,$project,$key,$review,$envelope,$currency,$scale,$estimate,$reserved,'',$now" + extraValues + ");";
        using var attempt = ChirpArticleSql.Command(connection, transaction, sql,
            ("$id", approved.AuthorizationId.ToString("N")), ("$plan", pending.Review.PlanSha256), ("$ordinal", intent.CoverageOrdinal),
            ("$policy", pending.PolicySha256), ("$source", policy.SourceApprovalSha256), ("$account", policy.AccountId), ("$project", policy.ProjectId),
            ("$key", approved.IdempotencyKey), ("$review", pending.Review.ReviewSha256), ("$envelope", JsonSerializer.Serialize(intent.Envelope with { ChirpAuthorizationId = approved.AuthorizationId })),
            ("$currency", intent.Currency), ("$scale", intent.Scale), ("$estimate", intent.EstimateMinorUnits), ("$reserved", approved.ReservedMinorUnits),
            ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()), ("$root", grant?.RootAuthorizationId.ToString("N")),
            ("$parent", grant?.ParentAuthorizationId.ToString("N")), ("$parentState", grant?.ParentQueueRowSha256),
            ("$grant", grant is null ? null : ChirpPolicyValidation.Hash(grant)), ("$grantId", grant?.GrantId.ToString("N")));
        await attempt.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
