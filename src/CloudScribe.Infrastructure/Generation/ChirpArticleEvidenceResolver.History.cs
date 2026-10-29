using System.Text.Json;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

internal sealed partial class ChirpArticleEvidenceResolver
{
    private static void RequireAttempts(ChirpHistoricalRange historical, string text, ChirpArticlePlanProposal plan,
        string accountId, string projectId, RecoveryFinancialSnapshot financial)
    {
        var rows = financial.Queue.Where(x => Equals(x[0], accountId)
            && Equals(x[1], GoogleGenerationProvider.SynthesizeOperationStableId)
            && x[2] is string key && !key.StartsWith(ChirpArticleService.KeyPrefix, StringComparison.Ordinal)).ToArray();
        HashSet<string> required = new(StringComparer.Ordinal);
        foreach (var recovery in financial.Recovery.Where(x => Guid.TryParse(x[2] as string, out var id) && id == historical.DocumentId))
        {
            if (!Equals(recovery[8], accountId) || !Equals(recovery[9], projectId)
                || !Guid.TryParse(recovery[3] as string, out var revision) || revision != historical.RevisionId
                || !Equals(recovery[4], historical.TextSha256) || !Equals(recovery[25], 1L))
                throw new InvalidOperationException("A historical recovery is pending, changed or belongs to another identity.");
            foreach (int column in new[] { 5, 11, 13 }) required.Add((string)recovery[column]!);
        }
        string? payload = null;
        try
        {
            payload = GoogleSpeechPlanCompiler.Compile(new SpeechPlan(plan.LanguageCode, [new SpeechText(text)], "historical-coverage-check"),
            new(plan.LanguageCode, plan.VoiceName, "MP3", 5_000)).PayloadSha256;
        }
        catch (InvalidOperationException) when (historical.Attempts.Count == 0) { /* A never-submitted whole article may exceed the legacy envelope. */ }
        foreach (var row in financial.QueueStates.Where(x => string.Equals(x.Account, accountId, StringComparison.Ordinal)
            && string.Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal)
            && !x.Key.StartsWith(ChirpArticleService.KeyPrefix, StringComparison.Ordinal)
            && string.Equals(x.PayloadSha256, payload, StringComparison.Ordinal) && x.Attempted == 1)) required.Add(row.Key);
        if (!required.IsSubsetOf(historical.Attempts.Select(x => x.IdempotencyKey)))
            throw new InvalidOperationException("The revised plan omits a historical root or recovery attempt.");
        foreach (var pin in historical.Attempts)
        {
            var matches = rows.Where(x => Equals(x[2], pin.IdempotencyKey)).ToArray();
            var state = financial.QueueStates.SingleOrDefault(x => string.Equals(x.Account, accountId, StringComparison.Ordinal)
                && string.Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal) && string.Equals(x.Key, pin.IdempotencyKey, StringComparison.Ordinal));
            if (matches.Length != 1 || state is null || state.Unresolved != 0 || state.Attempted != 1
                || !string.Equals(RecoveryFinancialSnapshot.Hash(matches[0]), pin.StateSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("A historical attempt is unresolved, missing or changed; no replacement request is permitted.");
            var recovered = financial.Recovery.Where(x => Equals(x[13], pin.IdempotencyKey)).ToArray();
            long retained;
            if (recovered.Length == 1) retained = (long)recovered[0][22]!;
            else
            {
                var spend = financial.Spend.Where(x => Equals(x[1], accountId) && Equals(x[8], state.PayloadSha256)
                    && Equals(x[10], "USD") && Equals(x[11], 2L)).ToArray();
                if (recovered.Length != 0 || spend.Length == 0) throw new InvalidOperationException("A retained historical reservation cannot be independently verified.");
                retained = spend.Max(x => (long)x[12]!);
            }
            if (retained != pin.RetainedReservationMinorUnits)
                throw new InvalidOperationException("A historical retained reservation differs from the reviewed plan.");
        }
    }

    private async Task RequireReusedAsync(SqliteConnection connection, SqliteTransaction transaction, DocumentSnapshot document,
        AdmittedChirpArticle admitted, ChirpReusedArticleAudio retained, RecoveryFinancialSnapshot financial, CancellationToken token)
    {
        var audio = retained.Audio;
        if (!Guid.TryParseExact(audio.LocalAttemptId, "N", out _) || retained.ParentDocumentId != document.Id
            || retained.ParentRevisionId != document.CurrentRevisionId || audio.AudioBytes is < 12 or > 256L * 1024 * 1024)
            throw new InvalidOperationException("Retained audio has invalid native coverage or identity.");
        var native = admitted.Lineage.History.Where(x => x.DocumentId == audio.DocumentId && x.RevisionId == audio.RevisionId
            && x.StartScalar == retained.Range.StartScalar && x.EndScalarExclusive == retained.Range.EndScalarExclusive
            && string.Equals(x.TextSha256, audio.TextSha256, StringComparison.Ordinal)).ToArray();
        if (native.Length != 1 || !string.Equals(audio.VoiceName, admitted.Proposal.VoiceName, StringComparison.Ordinal)
            || !string.Equals(audio.LanguageCode, admitted.Proposal.LanguageCode, StringComparison.Ordinal))
            throw new InvalidOperationException("Retained audio does not match the saved article's exact original child coverage.");
        byte[] bytes = await ChirpPolicyFiles.ReadAsync(Path.Combine(paths.RootDirectory, "accepted-article-audio", audio.LocalAttemptId + ".json"), 16384, token).ConfigureAwait(false);
        if (!string.Equals(ChirpPolicyFiles.Hash(bytes), retained.ReceiptSha256, StringComparison.Ordinal)
            || ChirpPolicyFiles.Parse<ArticleAcceptedAudio>(bytes) != audio)
            throw new InvalidOperationException("The retained accepted receipt changed.");
        RequireAcceptedQueue(audio, financial);
        byte[] media = await ChirpPolicyFiles.ReadAsync(audio.AudioPath, 256 * 1024 * 1024, token).ConfigureAwait(false);
        if (media.LongLength != audio.AudioBytes || !string.Equals(ChirpPolicyFiles.Hash(media), audio.AudioSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Retained accepted audio changed; it will not be regenerated automatically.");
    }

    internal static void RequireAcceptedQueue(ArticleAcceptedAudio audio, RecoveryFinancialSnapshot financial)
    {
        var matches = financial.QueueStates.Where(x => string.Equals(x.Account, audio.AccountId, StringComparison.Ordinal)
            && string.Equals(x.Operation, audio.OperationId, StringComparison.Ordinal) && string.Equals(x.Key, audio.IdempotencyKey, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || matches[0].Unresolved != 0 || matches[0].Attempted != 1
            || !string.Equals(matches[0].LocalAttempt, audio.LocalAttemptId, StringComparison.Ordinal)
            || !string.Equals(matches[0].DiagnosticCode, "google-accepted", StringComparison.Ordinal)
            || !string.Equals(matches[0].PayloadSha256, audio.PayloadSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Accepted audio has no exact durable terminal queue outcome.");
    }
}
