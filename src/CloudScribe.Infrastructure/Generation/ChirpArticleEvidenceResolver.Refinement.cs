using System.Text.Json;
using CloudScribe.Application.Logging;
using CloudScribe.Domain.Generation;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

internal sealed partial class ChirpArticleEvidenceResolver
{
    private async Task RequireRefinementAsync(SqliteConnection connection, SqliteTransaction transaction,
        AdmittedChirpArticle admitted, string account, string project, RecoveryFinancialSnapshot financial, CancellationToken token)
    {
        try { await RequireRefinementCoreAsync(connection, transaction, admitted, account, project, financial, token).ConfigureAwait(false); }
        catch (InvalidOperationException error) when (LocalRequestDiagnostics.Describe(error, DateTimeOffset.UtcNow).Reason == LocalRequestReason.ValidationRejected)
        { throw LocalRequestDiagnostics.Mark(error, LocalRequestReason.RefinementEvidenceChanged); }
    }

    private async Task RequireRefinementCoreAsync(SqliteConnection connection, SqliteTransaction transaction,
        AdmittedChirpArticle admitted, string account, string project, RecoveryFinancialSnapshot financial, CancellationToken token)
    {
        if (admitted.Proposal.Refinement is not { } refinement)
        {
            if (admitted.ReusedAudio.Any(x => x.SourcePlanSha256 is not null || x.SourceAuthorizationId is not null))
                throw new InvalidOperationException("Internal receipt reuse requires an exact independently reviewed refinement.");
            return;
        }
        if (refinement.Parent.Refinement is not null) throw new InvalidOperationException("Nested refinement is not supported.");
        using var cmd = ChirpArticleSql.Command(connection, transaction,
            "SELECT ProposalJson,LineageJson,ReusedAudioJson,AccountId,ProjectId FROM chirp_article_plans WHERE PlanSha256=$plan;", ("$plan", refinement.Parent.PlanSha256));
        AdmittedChirpArticle parent;
        using (var row = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(token).ConfigureAwait(false) || !string.Equals(row.GetString(3), account, StringComparison.Ordinal) || !string.Equals(row.GetString(4), project, StringComparison.Ordinal) || !string.Equals(row.GetString(0), JsonSerializer.Serialize(refinement.Parent), StringComparison.Ordinal)) throw new InvalidOperationException("Immutable refinement predecessor differs.");
            parent = new(refinement.Parent, ChirpPolicyFiles.Parse<CloudScribe.Application.Generation.ChirpPlanLineage>(System.Text.Encoding.UTF8.GetBytes(row.GetString(1))),
                ChirpPolicyFiles.Parse<ChirpReusedArticleAudio[]>(System.Text.Encoding.UTF8.GetBytes(row.GetString(2))), []);
        }
        _ = await ResolveSnapshotAsync(connection, transaction, parent, account, project, financial, token).ConfigureAwait(false);
        var attempts = await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_owned_attempts", token).ConfigureAwait(false);
        var failed = attempts.SingleOrDefault(x => Equals(x[0], refinement.FailedAuthorizationId.ToString("N")))
            ?? throw new InvalidOperationException("The exact failed predecessor is absent.");
        var part = refinement.Parent.Parts.Single(x => x.CoverageOrdinal == refinement.FailedCoverageOrdinal);
        var queue = financial.Queue.SingleOrDefault(x => Equals(x[0], account) && Equals(x[1], GoogleGenerationProvider.SynthesizeOperationStableId) && Equals(x[2], failed[7]));
        var state = financial.QueueStates.SingleOrDefault(x => string.Equals(x.Account, account, StringComparison.Ordinal) && string.Equals(x.Key, (string)failed[7]!, StringComparison.Ordinal) && string.Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal));
        if (failed.Length != 18 || !Equals(failed[1], refinement.Parent.PlanSha256) || !Equals(failed[2], (long)refinement.FailedCoverageOrdinal)
            || !Equals(failed[5], account) || !Equals(failed[6], project) || !Equals(failed[16], 1L) || !Equals(failed[17], refinement.FailedLocalAttemptId)
            || !string.Equals(RecoveryFinancialSnapshot.Hash(failed), refinement.FailedAttemptRowSha256, StringComparison.Ordinal) || queue is null
            || !string.Equals(RecoveryFinancialSnapshot.Hash(queue), refinement.FailedQueueRowSha256, StringComparison.Ordinal) || !Equals(queue[5], (long)GoogleErrorStatus.InvalidArgument)
            || queue[11] is not long observed || observed <= 0 || state is null || state.Attempted != 1 || state.Unresolved != 0
            || state.ProviderId is not null || !string.Equals(state.LocalAttempt, refinement.FailedLocalAttemptId, StringComparison.Ordinal) || !string.Equals(state.DiagnosticCode, "google-http-400", StringComparison.Ordinal) || !string.Equals(state.PayloadSha256, part.PayloadSha256, StringComparison.Ordinal) || state.PayloadBytes != part.PayloadBytes)
            throw new InvalidOperationException("Only the pinned consumed terminal HTTP400 invalid-argument range can be subdivided.");
        using (var accepted = ChirpArticleSql.Command(connection, transaction, """
            SELECT (SELECT COUNT(*) FROM chirp_article_receipts WHERE PlanSha256=$plan AND CoverageOrdinal=$ordinal)
              + (SELECT COUNT(*) FROM chirp_article_output_journals j JOIN chirp_article_owned_attempts a ON a.AuthorizationId=j.AuthorizationId
                 WHERE a.PlanSha256=$plan AND a.CoverageOrdinal=$ordinal);
            """, ("$plan", refinement.Parent.PlanSha256), ("$ordinal", refinement.FailedCoverageOrdinal)))
            if (Convert.ToInt64(await accepted.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("Accepted or recoverable output cannot be replaced.");
        foreach (var attempt in attempts.Where(x => Equals(x[1], refinement.Parent.PlanSha256)))
        {
            var q = financial.QueueStates.SingleOrDefault(x => string.Equals(x.Account, account, StringComparison.Ordinal) && string.Equals(x.Key, (string)attempt[7]!, StringComparison.Ordinal) && string.Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal));
            if (!Equals(attempt[16], 1L) || q is null || q.Unresolved != 0 || q.Attempted != 1)
                throw new InvalidOperationException("Pending or uncertain predecessor work prevents refinement.");
            if (string.Equals(q.DiagnosticCode, "google-accepted", StringComparison.Ordinal))
                await RequireFinalizedParentOutputAsync(connection, transaction, attempt, token).ConfigureAwait(false);
        }
        await RequireAllAcceptedParentReuseAsync(connection, transaction, admitted, refinement.Parent.PlanSha256, token).ConfigureAwait(false);
    }

    private static async Task RequireAllAcceptedParentReuseAsync(SqliteConnection connection, SqliteTransaction transaction,
        AdmittedChirpArticle admitted, string parentPlan, CancellationToken token)
    {
        using var receipts = ChirpArticleSql.Command(connection, transaction,
            "SELECT AuthorizationId,ReceiptSha256 FROM chirp_article_receipts WHERE PlanSha256=$plan ORDER BY CoverageOrdinal LIMIT 10001;", ("$plan", parentPlan));
        using var reader = await receipts.ExecuteReaderAsync(token).ConfigureAwait(false);
        HashSet<Guid> expected = [];
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            Guid id = Guid.ParseExact(reader.GetString(0), "N");
            if (expected.Count >= 10_000 || !expected.Add(id) || admitted.ReusedAudio.Count(x => string.Equals(x.SourcePlanSha256, parentPlan, StringComparison.Ordinal) && x.SourceAuthorizationId == id && string.Equals(x.ReceiptSha256, reader.GetString(1), StringComparison.Ordinal)) != 1)
                throw new InvalidOperationException("Every accepted predecessor segment must be reused exactly once.");
        }
        if (!expected.SetEquals(admitted.ReusedAudio.Where(x => x.SourcePlanSha256 is not null).Select(x => x.SourceAuthorizationId ?? Guid.Empty)))
            throw new InvalidOperationException("Refinement contains unverified internal receipt reuse.");
    }

    private static async Task RequireFinalizedParentOutputAsync(SqliteConnection connection, SqliteTransaction transaction,
        object?[] attempt, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction, """
            SELECT r.ReceiptJson,r.ReceiptSha256,j.ReceiptJson,j.ReceiptSha256
            FROM chirp_article_receipts r JOIN chirp_article_output_journals j ON j.AuthorizationId=r.AuthorizationId
            WHERE r.AuthorizationId=$id AND r.PlanSha256=$plan AND r.CoverageOrdinal=$ordinal;
            """, ("$id", attempt[0]), ("$plan", attempt[1]), ("$ordinal", attempt[2]));
        using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await row.ReadAsync(token).ConfigureAwait(false)
            || !string.Equals(row.GetString(0), row.GetString(2), StringComparison.Ordinal)
            || !string.Equals(row.GetString(1), row.GetString(3), StringComparison.Ordinal))
            throw LocalRequestDiagnostics.Mark(new InvalidOperationException("Recover the existing accepted output before refinement; a terminal success is not permission to generate it again."), LocalRequestReason.RefinementOutputRecoveryRequired);
        var receipt = ChirpPolicyFiles.Parse<ChirpSegmentAcceptedAudio>(System.Text.Encoding.UTF8.GetBytes(row.GetString(0)));
        if (!string.Equals(ChirpPolicyValidation.Hash(receipt), row.GetString(1), StringComparison.Ordinal))
            throw new InvalidOperationException("The existing accepted output journal differs from its final receipt.");
    }

    private async Task RequireInternalReusedAsync(SqliteConnection connection, SqliteTransaction transaction,
        AdmittedChirpArticle admitted, ChirpReusedArticleAudio retained, RecoveryFinancialSnapshot financial, CancellationToken token)
    {
        try { await RequireInternalReusedCoreAsync(connection, transaction, admitted, retained, financial, token).ConfigureAwait(false); }
        catch (InvalidOperationException error) when (LocalRequestDiagnostics.Describe(error, DateTimeOffset.UtcNow).Reason == LocalRequestReason.ValidationRejected)
        { throw LocalRequestDiagnostics.Mark(error, LocalRequestReason.RefinementEvidenceChanged); }
    }

    private async Task RequireInternalReusedCoreAsync(SqliteConnection connection, SqliteTransaction transaction,
        AdmittedChirpArticle admitted, ChirpReusedArticleAudio retained, RecoveryFinancialSnapshot financial, CancellationToken token)
    {
        if (admitted.Proposal.Refinement is not { } refinement || retained.SourceAuthorizationId is not { } id || id == Guid.Empty
            || !string.Equals(retained.SourcePlanSha256, refinement.Parent.PlanSha256, StringComparison.Ordinal)) throw new InvalidOperationException("Internal reuse lacks its exact predecessor.");
        using var cmd = ChirpArticleSql.Command(connection, transaction, """
            SELECT r.ReceiptJson,r.ReceiptSha256,a.Consumed,a.ClaimedLocalAttemptId,a.ChildKey,a.EnvelopeJson,a.AccountId,a.ProjectId,p.AccountId,p.ProjectId
            FROM chirp_article_receipts r JOIN chirp_article_owned_attempts a ON a.AuthorizationId=r.AuthorizationId
            JOIN chirp_article_plans p ON p.PlanSha256=r.PlanSha256
            WHERE r.AuthorizationId=$id AND r.PlanSha256=$plan;
            """, ("$id", id.ToString("N")), ("$plan", retained.SourcePlanSha256));
        ChirpSegmentAcceptedAudio receipt; GoogleGenerationSubmissionEnvelope envelope;
        using (var row = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("The accepted predecessor receipt is absent.");
            receipt = ChirpPolicyFiles.Parse<ChirpSegmentAcceptedAudio>(System.Text.Encoding.UTF8.GetBytes(row.GetString(0)));
            envelope = ChirpPolicyFiles.Parse<GoogleGenerationSubmissionEnvelope>(System.Text.Encoding.UTF8.GetBytes(row.GetString(5)));
            if (!string.Equals(ChirpPolicyValidation.Hash(receipt), retained.ReceiptSha256, StringComparison.Ordinal) || !string.Equals(row.GetString(1), retained.ReceiptSha256, StringComparison.Ordinal) || row.GetInt64(2) != 1
                || !string.Equals(receipt.Audio.LocalAttemptId, row.GetString(3), StringComparison.Ordinal) || !string.Equals(receipt.Audio.IdempotencyKey, row.GetString(4), StringComparison.Ordinal) || !string.Equals(row.GetString(6), retained.Audio.AccountId, StringComparison.Ordinal) || !string.Equals(row.GetString(6), row.GetString(8), StringComparison.Ordinal) || !string.Equals(row.GetString(7), row.GetString(9), StringComparison.Ordinal) || !string.Equals(envelope.AccountId, receipt.Audio.AccountId, StringComparison.Ordinal)
                || !string.Equals(envelope.VoiceName, receipt.Audio.VoiceName, StringComparison.Ordinal)
                || !string.Equals(envelope.AudioEncoding, "MP3", StringComparison.Ordinal)
                || envelope.ChirpAuthorizationId != id || envelope.RecoveryAuthorizationId is not null)
                throw new InvalidOperationException("The consumed predecessor receipt identity changed.");
        }
        var originalPart = refinement.Parent.Parts.Single(x => x.CoverageOrdinal == receipt.CoverageOrdinal);
        if (!string.Equals(receipt.PlanSha256, retained.SourcePlanSha256, StringComparison.Ordinal) || receipt.Range != originalPart.Range || receipt.Audio != retained.Audio
            || retained.Range with { ReusedReceiptSha256 = null } != receipt.Range || !string.Equals(retained.Range.ReusedReceiptSha256, retained.ReceiptSha256, StringComparison.Ordinal) || receipt.Audio.DocumentId != admitted.Proposal.DocumentId || receipt.Audio.RevisionId != admitted.Proposal.RevisionId
            || !string.Equals(receipt.Audio.TextSha256, originalPart.Range.TextSha256, StringComparison.Ordinal) || !string.Equals(receipt.Audio.PayloadSha256, originalPart.PayloadSha256, StringComparison.Ordinal) || !string.Equals(envelope.CompiledPayloadSha256, originalPart.PayloadSha256, StringComparison.Ordinal) || envelope.CompiledPayloadBytes != originalPart.PayloadBytes
            || !string.Equals(receipt.Audio.VoiceName, admitted.Proposal.VoiceName, StringComparison.Ordinal) || !string.Equals(receipt.Audio.LanguageCode, admitted.Proposal.LanguageCode, StringComparison.Ordinal) || !string.Equals(Path.GetFullPath(receipt.Audio.AudioPath), Path.Combine(paths.RootDirectory, "accepted-chirp-article-audio", id.ToString("N") + ".mp3"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Accepted internal audio does not match the immutable source range and exact voice.");
        RequireAcceptedQueue(receipt.Audio, financial);
        string? mapping = await ChirpArticleService.RequireReservedDispatchAuditAsync(connection, transaction, id, envelope, receipt.Audio.LocalAttemptId, token).ConfigureAwait(false);
        if (!string.Equals(mapping, receipt.ExecutionMappingSha256, StringComparison.Ordinal)) throw new InvalidOperationException("Accepted execution mapping changed.");
        byte[] media = await ChirpPolicyFiles.ReadAsync(receipt.Audio.AudioPath, 256 * 1024 * 1024, token).ConfigureAwait(false);
        if (media.LongLength != receipt.Audio.AudioBytes || !string.Equals(ChirpPolicyFiles.Hash(media), receipt.Audio.AudioSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Accepted internal audio bytes changed; never regenerate automatically.");
    }
}
