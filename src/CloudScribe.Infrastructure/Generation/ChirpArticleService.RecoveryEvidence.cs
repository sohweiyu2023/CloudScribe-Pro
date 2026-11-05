using System.Text;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private static async Task<ChirpArticleRecoveryChild?> ReadRecoveryChildAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid id, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT MappingJson,MappingSha256,ParentAuthorizationId,ChildSlot,JobId FROM chirp_article_split_mappings WHERE AuthorizationId=$id;", ("$id", id.ToString("N")));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        string json = reader.GetString(0);
        if (json.Length > 16384) throw new InvalidDataException("Bounded child mapping exceeds its source-review bound.");
        var mapping = ParseStored<ChirpArticleRecoveryChild>(json);
        if (mapping.AuthorizationId != id || !string.Equals(ChirpPolicyValidation.Hash(mapping), reader.GetString(1), StringComparison.Ordinal)
            || !string.Equals(mapping.ParentAuthorizationId.ToString("N"), reader.GetString(2), StringComparison.Ordinal)
            || mapping.ChildSlot != reader.GetInt32(3) || !string.Equals(mapping.JobId.ToString("N"), reader.GetString(4), StringComparison.Ordinal))
            throw new InvalidOperationException("Bounded child mapping changed.");
        return mapping;
    }

    private static async Task RequireSentenceRejectionAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, object?[] attempt, CancellationToken token)
    {
        if (attempt.Length != 18 || !Equals(attempt[16], 1L) || attempt[17] is not string local)
            throw new InvalidOperationException("An unclaimed or pending request cannot be subdivided.");
        var queues = await RecoveryFinancialSnapshot.ReadAsync(connection, transaction, token).ConfigureAwait(false);
        var queue = queues.Queue.SingleOrDefault(x => Equals(x[0], attempt[5]) && Equals(x[1], GoogleGenerationProvider.SynthesizeOperationStableId) && Equals(x[2], attempt[7]));
        var state = queues.QueueStates.SingleOrDefault(x => Equals(x.Account, attempt[5]) && Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId) && Equals(x.Key, attempt[7]));
        var envelope = ParseStored<GoogleGenerationSubmissionEnvelope>((string)attempt[9]!);
        if (queue is null || state is null || state.Attempted != 1 || state.Unresolved != 0 || state.ProviderId is not null
            || !string.Equals(state.LocalAttempt, local, StringComparison.Ordinal) || !string.Equals(state.DiagnosticCode, "google-http-400", StringComparison.Ordinal)
            || !Equals(queue[5], (long)GoogleErrorStatus.InvalidArgument) || queue[11] is not long observed || observed <= 0
            || !string.Equals(state.PayloadSha256, envelope.CompiledPayloadSha256, StringComparison.Ordinal) || state.PayloadBytes != envelope.CompiledPayloadBytes)
            throw new InvalidOperationException("Only the exact consumed confirmed InvalidArgument outcome permits bounded recovery.");
        using var output = ChirpArticleSql.Command(connection, transaction, """
            SELECT (SELECT COUNT(*) FROM chirp_article_output_journals WHERE AuthorizationId=$id)
                +(SELECT COUNT(*) FROM chirp_article_receipts WHERE AuthorizationId=$id)
                +(SELECT COUNT(*) FROM chirp_article_split_receipts WHERE AuthorizationId=$id);
            """, ("$id", attempt[0]));
        if (Convert.ToInt64(await output.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("Accepted or recoverable output must never be replaced by subdivision.");
        if (await HasNativeSentenceRejectionAsync(connection, transaction, attempt, envelope, observed, token).ConfigureAwait(false)) return;
        await RequireReviewedSentenceRejectionAsync(connection, transaction, policy, attempt, queue, envelope, token).ConfigureAwait(false);
    }

    private static async Task<bool> HasNativeSentenceRejectionAsync(SqliteConnection connection, SqliteTransaction transaction,
        object?[] attempt, GoogleGenerationSubmissionEnvelope envelope, long observed, CancellationToken token)
    {
        using var proof = ChirpArticleSql.Command(connection, transaction, """
            SELECT COUNT(*) FROM google_generation_sentence_rejections WHERE AccountId=$account AND OperationStableId=$operation
            AND IdempotencyKey=$key AND LocalAttemptId=$local AND PayloadSha256=$payload AND PayloadBytes=$bytes
            AND ErrorStatus=$status AND ValidationHint=$hint AND ObservedAtUnixMilliseconds=$observed;
            """, ("$account", attempt[5]), ("$operation", GoogleGenerationProvider.SynthesizeOperationStableId), ("$key", attempt[7]),
            ("$local", attempt[17]), ("$payload", envelope.CompiledPayloadSha256), ("$bytes", envelope.CompiledPayloadBytes),
            ("$status", (int)GoogleErrorStatus.InvalidArgument), ("$hint", (int)GoogleValidationMessageHint.SentenceLengthReported), ("$observed", observed));
        return Convert.ToInt64(await proof.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task RequireReviewedSentenceRejectionAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, object?[] attempt, object?[] queue, GoogleGenerationSubmissionEnvelope envelope, CancellationToken token)
    {
        var id = Guid.ParseExact((string)attempt[0]!, "N");
        var pin = policy.SentenceRecovery?.ReviewedHistoricalRejections.SingleOrDefault(x => x.AuthorizationId == id)
            ?? throw new InvalidOperationException("No native sentence-length observation or independently reviewed protected historical evidence exists.");
        string table = await AttemptTableAsync(connection, transaction, (string)attempt[0]!, token).ConfigureAwait(false);
        var rows = await ChirpArticleSql.ReadAsync(connection, transaction, table, token).ConfigureAwait(false);
        var full = rows.Single(x => Equals(x[0], attempt[0]));
        if (!string.Equals(pin.AttemptRowSha256, ChirpPolicyValidation.Hash(full), StringComparison.Ordinal)
            || !string.Equals(pin.QueueRowSha256, ChirpPolicyValidation.Hash(queue), StringComparison.Ordinal)
            || !string.Equals(pin.PayloadSha256, envelope.CompiledPayloadSha256, StringComparison.Ordinal) || pin.PayloadBytes != envelope.CompiledPayloadBytes)
            throw new InvalidOperationException("The explicitly reviewed historical rejection differs from current immutable evidence.");
    }

    private static async Task<ChirpArticlePlanPart> RecoveryPartAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, ChirpArticleRecoveryChild child, CancellationToken token)
    {
        if (policy.SentenceRecovery is not { } grant || !ChirpPolicyValidation.IsReviewedRecoveryGrant(policy, child.RecoveryGrantSha256)
            || child.Depth < 1 || child.Depth > grant.MaximumDepth || child.ChildSlot is < 1 or > 2)
            throw new InvalidOperationException("The child is outside the independently admitted bounded recovery grant.");
        var article = policy.Articles.Single(x => string.Equals(x.Proposal.PlanSha256, child.PlanSha256, StringComparison.Ordinal));
        var original = article.Proposal.Parts.Single(x => x.CoverageOrdinal == child.CoverageOrdinal && x.Range.ReusedReceiptSha256 is null);
        if (child.Depth >= original.Range.EndScalarExclusive - original.Range.StartScalar
            || child.Range.ReusedReceiptSha256 is not null || child.Range.StartScalar < original.Range.StartScalar
            || child.Range.EndScalarExclusive > original.Range.EndScalarExclusive || child.Range.EndScalarExclusive <= child.Range.StartScalar
            || child.Range.EndScalarExclusive - child.Range.StartScalar >= original.Range.EndScalarExclusive - original.Range.StartScalar)
            throw new InvalidOperationException("The child cannot replace accepted coverage or escape its original exact range.");
        var document = await ChirpArticleEvidenceResolver.ReadDocumentAsync(connection, transaction, article.Proposal.DocumentId, token).ConfigureAwait(false);
        string text = ChirpNarrationPlanner.Slice(document.Text, child.Range);
        var wire = GoogleSpeechPlanCompiler.Compile(new(article.Proposal.LanguageCode, [new SpeechText(text)], ChirpBoundedSentenceSplit.Version),
            new(article.Proposal.LanguageCode, article.Proposal.VoiceName, "MP3", 5000) { SentenceMarkers = GoogleSpeechPlanCompiler.ExistingSentenceMarkers(text) });
        if (!string.Equals(wire.PayloadSha256, child.PayloadSha256, StringComparison.Ordinal) || wire.Payload.Length != child.PayloadBytes)
            throw new InvalidOperationException("Child source, sentence markers or exact wire identity changed.");
        return new(child.CoverageOrdinal, child.Range, child.PayloadSha256, child.PayloadBytes);
    }
}
