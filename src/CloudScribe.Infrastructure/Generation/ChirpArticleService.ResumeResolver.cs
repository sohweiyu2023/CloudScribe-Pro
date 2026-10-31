using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private async Task<ChirpReservationResumePolicy?> ResumePolicyAsync(Guid? authorization, CancellationToken token)
    {
        var value = resumeAdmission is null ? null : await resumeAdmission.LoadVerifiedAsync(token).ConfigureAwait(false);
        if (value is null || authorization is not null && value.AuthorizationId != authorization) return null;
        var owned = ChirpPolicyFiles.Parse<ChirpReservationResumePolicy>(JsonSerializer.SerializeToUtf8Bytes(value));
        ValidateResumeScope(owned);
        return owned;
    }

    private async Task RequireResumeCurrentAsync(ChirpReservationResumePolicy expected, CancellationToken token)
    {
        var current = await ResumePolicyAsync(expected.AuthorizationId, token).ConfigureAwait(false);
        if (current is null || !string.Equals(ChirpPolicyValidation.Hash(current), ChirpPolicyValidation.Hash(expected), StringComparison.Ordinal))
            throw LocalRequestDiagnostics.Mark(ResumeRejected(), LocalRequestReason.AdmissionChanged, admissionExpiresUtc: expected.ExpiresAtUtc);
    }

    private async Task<ResumeState> ResolveResumeAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpReservationResumePolicy policy, GoogleGenerationSubmissionEnvelope envelope, bool requireMapping, CancellationToken token)
    {
        ValidateResumeScope(policy);
        if (envelope != policy.ExecutionEnvelope) throw ResumeRejected(LocalRequestReason.ReservedEnvelopeChanged);
        var rows = await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_owned_attempts", token).ConfigureAwait(false);
        var row = rows.SingleOrDefault(x => Equals(x[0], policy.AuthorizationId.ToString("N")));
        if (row is not { Length: 18 } || !Equals(row[16], 0L) || row[17] is not null) throw ResumeRejected();
        if (!string.Equals(RecoveryFinancialSnapshot.Hash(row), policy.OriginalAttemptRowSha256, StringComparison.Ordinal)
            || !Equals(row[3], ChirpPolicyValidation.Hash(policy.HistoricalPolicy))
            || !Equals(row[15], policy.OriginalApprovedAtUtc.ToUnixTimeMilliseconds())
            || !Equals(row[9], JsonSerializer.Serialize(policy.OriginalEnvelope))) throw ResumeRejected(LocalRequestReason.ResumeHistoryChanged);
        // Validate the historical policy at its actual reservation time, never as current execution authority.
        ChirpPolicyValidation.RequireValid(policy.HistoricalPolicy, DateTimeOffset.FromUnixTimeMilliseconds((long)row[15]!));
        CurrentFinancial financial;
        try { financial = await ReadFinancialAsync(connection, transaction, policy.HistoricalPolicy, token).ConfigureAwait(false); }
        catch (InvalidOperationException error) { throw LocalRequestDiagnostics.Mark(error, LocalRequestReason.FinancialChanged, admissionExpiresUtc: policy.ExpiresAtUtc); }
        if (!string.Equals(financial.Sha256, policy.FinancialSha256, StringComparison.Ordinal) || !Equals(row[14], financial.Sha256)) throw ResumeRejected(LocalRequestReason.FinancialChanged);
        await RequireReadyAsync(connection, transaction, financial, policy.AuthorizationId, token).ConfigureAwait(false);
        var queue = financial.Old.Queue.SingleOrDefault(x => Equals(x[0], row[5]) && Equals(x[1], GoogleGenerationProvider.SynthesizeOperationStableId) && Equals(x[2], row[7]));
        if (queue is null || !string.Equals(RecoveryFinancialSnapshot.Hash(queue), policy.CleanQueueRowSha256, StringComparison.Ordinal)) throw ResumeRejected();
        await RequireNoReservedOutcomeAsync(connection, transaction, row, token).ConfigureAwait(false);
        using (var dispatch = ChirpArticleSql.Command(connection, transaction,
            "SELECT COUNT(*) FROM chirp_reserved_dispatch_mappings WHERE AuthorizationId=$id;", ("$id", policy.AuthorizationId.ToString("N"))))
            if (Convert.ToInt64(await dispatch.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0) throw ResumeRejected();
        var clean = financial.Old.QueueStates.Single(x => Equals(x.Account, row[5]) && Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId) && Equals(x.Key, row[7]));
        if (clean.Attempted != 0 || clean.Unresolved != 0 || clean.LocalAttempt is not null || clean.ProviderId is not null
            || clean.DiagnosticCode is not null || clean.PayloadSha256 is not null || clean.PayloadBytes is not null) throw ResumeRejected();
        if (!string.Equals(await ResumeProviderEvidenceAsync(connection, transaction, policy.OriginalEnvelope, policy.HistoricalPolicy.ProjectId, policy.CatalogSnapshotId, policy.CatalogExpiresAtUtc, token).ConfigureAwait(false), policy.ProviderEvidenceSha256, StringComparison.Ordinal)) throw ResumeRejected(LocalRequestReason.ResumeProviderChanged);
        var article = policy.HistoricalPolicy.Articles.Single(x => Equals(x.Proposal.PlanSha256, row[1]));
        var part = article.Proposal.Parts.Single(x => x.CoverageOrdinal == (int)(long)row[2]!);
        var source = await _evidence.ResolveAsync(connection, transaction, policy.HistoricalPolicy, article.Proposal.DocumentId, financial.Old, token, requirePersistedLegacy: true).ConfigureAwait(false);
        var compiled = GoogleSpeechPlanCompiler.Compile(new CloudScribe.Domain.Generation.SpeechPlan(article.Proposal.LanguageCode,
            [new CloudScribe.Domain.Generation.SpeechText(ChirpNarrationPlanner.Slice(source.Document.Text, part.Range))], ChirpNarrationPlanner.Version),
            new(article.Proposal.LanguageCode, article.Proposal.VoiceName, "MP3", 5000));
        _ = await CurrentAsync(connection, transaction, policy.HistoricalPolicy, new(article.Proposal.DocumentId, part.CoverageOrdinal,
            policy.HistoricalPolicy.ProjectId, policy.OriginalEnvelope with { ChirpAuthorizationId = null }, compiled.Payload,
            (string)row[10]!, (int)(long)row[11]!, (long)row[12]!), token).ConfigureAwait(false);
        await RequireStoredResumeMappingAsync(connection, transaction, policy, requireMapping, token).ConfigureAwait(false);
        return new(policy, row, financial, new((string)row[7]!, GoogleGenerationSpendAuthorization.Create(envelope,
            (string)row[10]!, (int)(long)row[11]!, (long)row[12]!, (long)row[13]!)), source.Document, article.Proposal, part, compiled.Payload);
    }

    private static InvalidOperationException ResumeRejected(LocalRequestReason reason = LocalRequestReason.ReservedRequestIneligible) => LocalRequestDiagnostics.Mark(
        new InvalidOperationException("Exact reserved catalog resume evidence is unavailable, changed or no longer eligible. No new reservation or retry was authorized."), reason);

    private void ValidateResumeScope(ChirpReservationResumePolicy policy)
    {
        try { ChirpReservationResumeValidation.RequireValid(policy, clock.GetUtcNow()); }
        catch (InvalidDataException error)
        {
            bool expired = policy.ExpiresAtUtc <= clock.GetUtcNow() || policy.CatalogExpiresAtUtc <= clock.GetUtcNow();
            throw LocalRequestDiagnostics.Mark(error, expired ? LocalRequestReason.ResumeExpired : LocalRequestReason.ResumePolicyMalformed,
                admissionExpiresUtc: policy.ExpiresAtUtc, capabilityExpiresUtc: policy.CatalogExpiresAtUtc);
        }
    }

    private sealed record ResumeState(ChirpReservationResumePolicy Policy, object?[] Row, CurrentFinancial Financial,
        AuthorizedSegment Authorized, CloudScribe.Application.Documents.DocumentSnapshot Document, ChirpArticlePlanProposal Proposal,
        ChirpArticlePlanPart Part, ReadOnlyMemory<byte> Payload);
}
