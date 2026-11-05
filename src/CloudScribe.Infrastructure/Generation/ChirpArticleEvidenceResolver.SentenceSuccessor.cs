using System.Text;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using CloudScribe.Domain.Generation;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

internal sealed partial class ChirpArticleEvidenceResolver
{
    private static async Task<AdmittedChirpArticle> ReadSentenceParentAsync(SqliteConnection connection, SqliteTransaction transaction,
        AdmittedChirpArticle admitted, string account, CancellationToken token)
    {
        var parent = admitted.Proposal.SentenceSuccessor?.Parent ?? throw new InvalidOperationException("Sentence predecessor is absent.");
        if (parent.InputFormat is not null || parent.SentenceSuccessor is not null || admitted.Proposal.Refinement is not null)
            throw new InvalidOperationException("Competing or nested sentence successors are not supported.");
        using var cmd = ChirpArticleSql.Command(connection, transaction,
            "SELECT ProposalJson,LineageJson,ReusedAudioJson,AccountId FROM chirp_article_plans WHERE PlanSha256=$plan;", ("$plan", parent.PlanSha256));
        using var row = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await row.ReadAsync(token).ConfigureAwait(false) || !string.Equals(row.GetString(3), account, StringComparison.Ordinal)
            || !string.Equals(row.GetString(0), JsonSerializer.Serialize(parent), StringComparison.Ordinal))
            throw new InvalidOperationException("Immutable sentence predecessor differs.");
        return new(parent, ChirpPolicyFiles.Parse<ChirpPlanLineage>(Encoding.UTF8.GetBytes(row.GetString(1))),
            ChirpPolicyFiles.Parse<ChirpReusedArticleAudio[]>(Encoding.UTF8.GetBytes(row.GetString(2))), []);
    }

    private async Task RequireSentenceSuccessorAsync(SqliteConnection connection, SqliteTransaction transaction,
        AdmittedChirpArticle admitted, string account, string project, RecoveryFinancialSnapshot financial, CancellationToken token)
    {
        var parent = await ReadSentenceParentAsync(connection, transaction, admitted, account, token).ConfigureAwait(false);
        using (var identity = ChirpArticleSql.Command(connection, transaction,
            "SELECT ProjectId FROM chirp_article_plans WHERE PlanSha256=$plan;", ("$plan", parent.Proposal.PlanSha256)))
            if (!string.Equals((string?)await identity.ExecuteScalarAsync(token).ConfigureAwait(false), project, StringComparison.Ordinal))
                throw new InvalidOperationException("Sentence predecessor project changed.");
        _ = await ResolveSnapshotAsync(connection, transaction, parent, account, project, financial, token).ConfigureAwait(false);
        foreach (var inherited in parent.ReusedAudio)
            if (admitted.ReusedAudio.Count(x => x == inherited) != 1)
                throw new InvalidOperationException("Sentence continuation cannot drop or change inherited accepted audio.");
        var attempts = await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_owned_attempts", token).ConfigureAwait(false);
        HashSet<Guid> accepted = [];
        foreach (var attempt in attempts.Where(x => Equals(x[1], parent.Proposal.PlanSha256)))
        {
            if (attempt.Length != 18) throw new InvalidOperationException("Predecessor attempt shape changed.");
            var part = parent.Proposal.Parts.Single(x => x.CoverageOrdinal == (int)(long)attempt[2]!);
            var state = financial.QueueStates.SingleOrDefault(x => string.Equals(x.Account, account, StringComparison.Ordinal)
                && string.Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal) && Equals(x.Key, attempt[7]));
            if (attempt.Length != 18 || !Equals(attempt[5], account) || !Equals(attempt[6], project) || !Equals(attempt[16], 1L)
                || state is null || state.Attempted != 1 || state.Unresolved != 0 || !Equals(state.LocalAttempt, attempt[17])
                || !string.Equals(state.PayloadSha256, part.PayloadSha256, StringComparison.Ordinal) || state.PayloadBytes != part.PayloadBytes)
                throw new InvalidOperationException("Pending, uncertain or changed predecessor work prevents sentence continuation.");
            if (string.Equals(state.DiagnosticCode, "google-accepted", StringComparison.Ordinal))
            {
                await RequireFinalizedParentOutputAsync(connection, transaction, attempt, token).ConfigureAwait(false);
                Guid id = Guid.ParseExact((string)attempt[0]!, "N"); accepted.Add(id);
                if (admitted.ReusedAudio.Count(x => x.SourceAuthorizationId == id && string.Equals(x.SourcePlanSha256, parent.Proposal.PlanSha256, StringComparison.Ordinal)) != 1)
                    throw new InvalidOperationException("Every accepted predecessor output must be retained exactly once.");
            }
            else if (!string.Equals(state.DiagnosticCode, "google-http-400", StringComparison.Ordinal) || state.ProviderId is not null
                || financial.Queue.Count(x => Equals(x[0], account) && Equals(x[1], GoogleGenerationProvider.SynthesizeOperationStableId)
                    && Equals(x[2], attempt[7]) && Equals(x[5], (long)GoogleErrorStatus.InvalidArgument) && x[11] is long observed && observed > 0) != 1)
                throw new InvalidOperationException("Only confirmed terminal invalid-argument failures permit this new sentence proposal.");
            else
            {
                using var outputs = ChirpArticleSql.Command(connection, transaction, """
                    SELECT (SELECT COUNT(*) FROM chirp_article_receipts WHERE AuthorizationId=$id OR (PlanSha256=$plan AND CoverageOrdinal=$ordinal))
                      + (SELECT COUNT(*) FROM chirp_article_output_journals j JOIN chirp_article_owned_attempts a ON a.AuthorizationId=j.AuthorizationId
                         WHERE a.AuthorizationId=$id OR (a.PlanSha256=$plan AND a.CoverageOrdinal=$ordinal));
                    """, ("$id", attempt[0]), ("$plan", parent.Proposal.PlanSha256), ("$ordinal", attempt[2]));
                if (Convert.ToInt64(await outputs.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0)
                    throw LocalRequestDiagnostics.Mark(new InvalidOperationException("Failed predecessor has accepted or recoverable output; never regenerate it."), LocalRequestReason.RefinementOutputRecoveryRequired);
            }
        }
        var direct = admitted.ReusedAudio.Where(x => string.Equals(x.SourcePlanSha256, parent.Proposal.PlanSha256, StringComparison.Ordinal)).ToArray();
        if (!accepted.SetEquals(direct.Select(x => x.SourceAuthorizationId ?? Guid.Empty))
            || admitted.ReusedAudio.Any(x => !parent.ReusedAudio.Contains(x) && !direct.Contains(x)))
            throw new InvalidOperationException("Sentence continuation contains unverified or duplicate accepted audio.");
    }
}
