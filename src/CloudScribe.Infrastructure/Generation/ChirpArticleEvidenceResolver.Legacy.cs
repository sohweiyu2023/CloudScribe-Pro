using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

internal sealed partial class ChirpArticleEvidenceResolver
{
    private static async Task RequireMissingLegacyAsync(SqliteConnection connection, SqliteTransaction transaction,
        AdmittedChirpArticle article, ChirpArticleAdmissionPolicy policy, RecoveryFinancialSnapshot financial, CancellationToken token)
    {
        if (!financial.QueueStates.Any(x => string.Equals(x.Account, policy.AccountId, StringComparison.Ordinal)
            && string.Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal)
            && !x.Key.StartsWith(ChirpArticleService.KeyPrefix, StringComparison.Ordinal) && x.Attempted == 1 && x.PayloadSha256 is null)) return;
        foreach (var history in article.Lineage.History.Where(x => x.Attempts.Count == 0))
        {
            bool overlaps = article.Proposal.Parts.Any(x => article.AllowedSegments.Any(y => y.CoverageOrdinal == x.CoverageOrdinal)
                && x.Range.StartScalar < history.EndScalarExclusive && history.StartScalar < x.Range.EndScalarExclusive);
            if (!overlaps) continue;
            var child = await ReadDocumentAsync(connection, transaction, history.DocumentId, token).ConfigureAwait(false);
            GoogleSpeechCompilation compiled;
            try
            {
                compiled = GoogleSpeechPlanCompiler.Compile(new SpeechPlan(article.Proposal.LanguageCode, [new SpeechText(child.Text)], "historical-coverage-check"),
                    new(article.Proposal.LanguageCode, article.Proposal.VoiceName, "MP3", 5000));
            }
            catch (InvalidOperationException) { continue; } // A never-submitted whole article can exceed the old envelope.
            if (financial.Spend.Any(x => Equals(x[1], policy.AccountId) && Equals(x[8], compiled.PayloadSha256))
                && !(policy.LegacyAssociations ?? []).Any(x => string.Equals(x.PlanSha256, article.Proposal.PlanSha256, StringComparison.Ordinal)
                    && x.DocumentId == history.DocumentId && x.RevisionId == history.RevisionId))
                throw new InvalidOperationException("Missing historical wire metadata and a retained native reservation require a separately reviewed association before overlapping requests.");
        }
    }

    private static async Task<IReadOnlyList<ChirpLegacyAttemptAssociation>> StoredLegacyAsync(SqliteConnection connection,
        SqliteTransaction transaction, string plan, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT AssociationJson,AssociationSha256,FirstPolicySha256,FirstSourceApprovalSha256 FROM chirp_article_legacy_associations WHERE PlanSha256=$plan LIMIT 201;", ("$plan", plan));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        List<ChirpLegacyAttemptAssociation> result = [];
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (result.Count >= 200 || reader.GetString(0).Length > 16384) throw new InvalidDataException("Historical audit exceeds its inspection bound.");
            var a = ChirpPolicyFiles.Parse<ChirpLegacyAttemptAssociation>(System.Text.Encoding.UTF8.GetBytes(reader.GetString(0)));
            if (!string.Equals(a.PlanSha256, plan, StringComparison.Ordinal) || !string.Equals(ChirpPolicyValidation.Hash(a), reader.GetString(1)
, StringComparison.Ordinal) || !ChirpPolicyValidation.IsHash(a.ReviewedEvidenceSha256) || !ChirpPolicyValidation.IsHash(reader.GetString(2))
                || !ChirpPolicyValidation.IsHash(reader.GetString(3)) || result.Any(x => string.Equals(x.IdempotencyKey, a.IdempotencyKey, StringComparison.Ordinal) || x.SpendAuthorizationId == a.SpendAuthorizationId))
                throw new InvalidOperationException("Stored historical association provenance is ambiguous or changed.");
            result.Add(a);
        }
        return result;
    }

    private static async Task RequireLegacyAsync(SqliteConnection connection, SqliteTransaction transaction,
        AdmittedChirpArticle article, string account, string project, RecoveryFinancialSnapshot financial,
        IReadOnlyList<ChirpLegacyAttemptAssociation> associations, bool requirePersisted, CancellationToken token)
    {
        foreach (var a in associations)
        {
            var child = await ReadDocumentAsync(connection, transaction, a.DocumentId, token).ConfigureAwait(false);
            var native = article.Lineage.History.SingleOrDefault(x => x.DocumentId == a.DocumentId && x.RevisionId == a.RevisionId
                && x.StartScalar == a.StartScalar && x.EndScalarExclusive == a.EndScalarExclusive && string.Equals(x.TextSha256, a.TextSha256, StringComparison.Ordinal));
            if (native is null || !string.Equals(a.PlanSha256, article.Proposal.PlanSha256, StringComparison.Ordinal) || !string.Equals(a.AccountId, account, StringComparison.Ordinal) || !string.Equals(a.ProjectId, project
, StringComparison.Ordinal) || !string.Equals(a.OperationId, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal) || child.CurrentRevisionId != a.RevisionId
                || !string.Equals(ArticleAudioManifest.HashText(child.Text), a.TextSha256, StringComparison.Ordinal) || a.RetainedReservationMinorUnits < 1
                || native.Attempts.Any(x => string.Equals(x.IdempotencyKey, a.IdempotencyKey, StringComparison.Ordinal)))
                throw new InvalidOperationException("Supplemental historical native coverage changed.");
            var compiled = GoogleSpeechPlanCompiler.Compile(new SpeechPlan(article.Proposal.LanguageCode, [new SpeechText(child.Text)], "historical-coverage-check"),
                new(article.Proposal.LanguageCode, article.Proposal.VoiceName, "MP3", 5000));
            var queue = financial.Queue.Where(x => Equals(x[0], account) && Equals(x[1], a.OperationId) && Equals(x[2], a.IdempotencyKey)).ToArray();
            var state = financial.QueueStates.SingleOrDefault(x => string.Equals(x.Account, account, StringComparison.Ordinal) && string.Equals(x.Operation, a.OperationId, StringComparison.Ordinal) && string.Equals(x.Key, a.IdempotencyKey, StringComparison.Ordinal));
            var spend = financial.Spend.Where(x => Guid.TryParse(x[0] as string, out var id) && id == a.SpendAuthorizationId).ToArray();
            if (queue.Length != 1 || state is null || state.Attempted != 1 || state.Unresolved != 0
                || state.PayloadSha256 is not null || state.PayloadBytes is not null || string.Equals(state.DiagnosticCode, "google-accepted"
, StringComparison.Ordinal) || !string.Equals(RecoveryFinancialSnapshot.Hash(queue[0]), a.QueueRowSha256, StringComparison.Ordinal) || spend.Length != 1
                || !string.Equals(RecoveryFinancialSnapshot.Hash(spend[0]), a.SpendRowSha256
, StringComparison.Ordinal) || !Equals(spend[0][1], account) || !Equals(spend[0][6], article.Proposal.VoiceName) || !Equals(spend[0][7], "MP3")
                || !Equals(spend[0][8], compiled.PayloadSha256) || !Equals(spend[0][9], (long)compiled.Payload.Length)
                || !Equals(spend[0][10], "USD") || !Equals(spend[0][11], 2L)
                || !Equals(spend[0][12], a.RetainedReservationMinorUnits)
                || !string.Equals(compiled.PayloadSha256, a.OriginalPayloadSha256, StringComparison.Ordinal) || compiled.Payload.Length != a.OriginalPayloadBytes)
                throw new InvalidOperationException("The separately reviewed legacy queue/spend association changed; all holds remain retained.");
        }
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT AssociationJson,AssociationSha256 FROM chirp_article_legacy_associations WHERE PlanSha256=$plan LIMIT 201;", ("$plan", article.Proposal.PlanSha256));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        HashSet<string> stored = new(StringComparer.Ordinal);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (stored.Count >= 200 || reader.GetString(0).Length > 16384) throw new InvalidDataException("Historical association audit exceeds its bound.");
            var item = ChirpPolicyFiles.Parse<ChirpLegacyAttemptAssociation>(System.Text.Encoding.UTF8.GetBytes(reader.GetString(0)));
            string hash = ChirpPolicyValidation.Hash(item);
            if (!string.Equals(reader.GetString(1), hash, StringComparison.Ordinal) || !stored.Add(hash) || !associations.Contains(item))
                throw new InvalidOperationException("An immutable historical association was omitted or replaced by current admission.");
        }
        if (requirePersisted && associations.Any(x => !stored.Contains(ChirpPolicyValidation.Hash(x))))
            throw new InvalidOperationException("The historical association audit was not committed with this request.");
    }
}
