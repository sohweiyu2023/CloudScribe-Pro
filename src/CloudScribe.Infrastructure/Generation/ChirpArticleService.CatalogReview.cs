using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using CloudScribe.Domain.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    /// <summary>
    /// Inspects a retained reservation without using its expired policy as authority. This preview cannot
    /// reserve, admit a mapping, activate a runtime request or dispatch. Fresh evidence remains subject to
    /// independent trusted admission and final transactional claim validation.
    /// </summary>
    public async Task<ChirpReservedCatalogReview> InspectReservedCatalogCandidateAsync(Guid authorizationId,
        ChirpRequestIntent candidate, Guid candidateCatalogId, DateTimeOffset candidateCatalogExpiresUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (authorizationId == Guid.Empty || candidateCatalogId == Guid.Empty
            || candidateCatalogExpiresUtc.Offset != TimeSpan.Zero || candidateCatalogExpiresUtc <= clock.GetUtcNow())
            throw LocalRequestDiagnostics.Mark(new InvalidOperationException("Fresh catalog evidence is required for inspection."),
                LocalRequestReason.CapabilityStale, capabilityExpiresUtc: candidateCatalogExpiresUtc);
        candidate = candidate with { CompiledPayload = candidate.CompiledPayload.ToArray() };
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var attempts = await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_owned_attempts", cancellationToken).ConfigureAwait(false);
        var row = attempts.SingleOrDefault(x => Equals(x[0], authorizationId.ToString("N")));
        if (row is not { Length: 18 } || !Equals(row[16], 0L) || row[17] is not null)
            throw RevalidationRejected(LocalRequestReason.ReservedRequestIneligible);
        var original = ChirpPolicyFiles.Parse<GoogleGenerationSubmissionEnvelope>(System.Text.Encoding.UTF8.GetBytes((string)row[9]!));
        var old = await RecoveryFinancialSnapshot.ReadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var queue = old.Queue.SingleOrDefault(x => Equals(x[0], row[5]) && Equals(x[1], GoogleGenerationProvider.SynthesizeOperationStableId) && Equals(x[2], row[7]));
        var state = old.QueueStates.SingleOrDefault(x => string.Equals(x.Account, (string)row[5]!, StringComparison.Ordinal)
            && string.Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal)
            && string.Equals(x.Key, (string)row[7]!, StringComparison.Ordinal));
        if (queue is null || state is null || state.Attempted != 0 || state.Unresolved != 0 || state.ProviderId is not null
            || state.LocalAttempt is not null || state.DiagnosticCode is not null || state.PayloadSha256 is not null
            || state.PayloadBytes is not null)
            throw RevalidationRejected(LocalRequestReason.ReservedRequestIneligible);
        await RequireNoReservedOutcomeAsync(connection, transaction, row, cancellationToken).ConfigureAwait(false);
        if (!Equals(row[5], candidate.Envelope.AccountId) || !Equals(row[6], candidate.ProjectId)
            || !Equals(row[10], candidate.Currency) || !Equals(row[11], (long)candidate.Scale)
            || !Equals(row[12], candidate.EstimateMinorUnits) || (long)row[13]! < candidate.EstimateMinorUnits
            || candidate.Envelope.ChirpAuthorizationId is not null || candidate.Envelope.RecoveryAuthorizationId is not null
            || original.ChirpAuthorizationId != authorizationId || original.RecoveryAuthorizationId is not null)
            throw RevalidationRejected(LocalRequestReason.ReservedEnvelopeChanged);
        var envelope = candidate.Envelope with { ChirpAuthorizationId = authorizationId };
        if ((envelope with { RequestRevision = original.RequestRevision }) != original)
            throw RevalidationRejected(LocalRequestReason.ReservedEnvelopeChanged);
        var stored = await StoredAsync(connection, transaction, (string)row[1]!, cancellationToken).ConfigureAwait(false);
        if (stored.Article.Proposal.DocumentId != candidate.DocumentId || candidate.CoverageOrdinal != (int)(long)row[2]!)
            throw RevalidationRejected(LocalRequestReason.ReservedSourceChanged);
        var current = await _evidence.ResolveSnapshotAsync(connection, transaction, stored.Article, stored.AccountId, stored.ProjectId, old, cancellationToken).ConfigureAwait(false);
        var part = stored.Article.Proposal.Parts.Single(x => x.CoverageOrdinal == candidate.CoverageOrdinal);
        var payload = GoogleSpeechPlanCompiler.Compile(new SpeechPlan(stored.Article.Proposal.LanguageCode,
            [new SpeechText(ChirpNarrationPlanner.Slice(current.Document.Text, part.Range))], ChirpNarrationPlanner.Version),
            new(stored.Article.Proposal.LanguageCode, stored.Article.Proposal.VoiceName, "MP3", 5_000)).Payload;
        if (!candidate.CompiledPayload.Span.SequenceEqual(payload.Span)
            || !string.Equals(ChirpPolicyFiles.Hash(payload.Span), original.CompiledPayloadSha256, StringComparison.Ordinal)
            || payload.Length != original.CompiledPayloadBytes)
            throw RevalidationRejected(LocalRequestReason.ReservedPayloadChanged);
        return BuildCatalogPreview(authorizationId, row, queue, old, attempts, original, envelope, candidate, candidateCatalogId, candidateCatalogExpiresUtc);
    }

    private static ChirpReservedCatalogReview BuildCatalogPreview(Guid authorizationId, object?[] row, object?[] queue,
        RecoveryFinancialSnapshot old, IReadOnlyList<object?[]> attempts, GoogleGenerationSubmissionEnvelope original,
        GoogleGenerationSubmissionEnvelope envelope, ChirpRequestIntent candidate, Guid candidateCatalogId, DateTimeOffset candidateCatalogExpiresUtc)
    {
        string attemptHash = RecoveryFinancialSnapshot.Hash(row), queueHash = RecoveryFinancialSnapshot.Hash(queue);
        // Inspection digest only. Final admission must independently reconcile the complete financial history.
        string financialHash = ChirpPolicyValidation.Hash(new { old.Queue, old.Spend, old.Recovery, old.Scopes, old.Transitions, attempts });
        string digest = ChirpPolicyValidation.Hash(new
        {
            Kind = "reserved-catalog-preview-no-authority-v1",
            authorizationId,
            attemptHash,
            queueHash,
            financialHash,
            original,
            envelope,
            candidateCatalogId,
            candidateCatalogExpiresUtc,
            candidate.DocumentId,
            candidate.CoverageOrdinal,
            candidate.ProjectId,
            candidate.Currency,
            candidate.Scale,
            candidate.EstimateMinorUnits
        });
        return new(authorizationId, (string)row[7]!, attemptHash, queueHash, financialHash, original, envelope,
            candidateCatalogId, candidateCatalogExpiresUtc, digest);
    }

    private static InvalidOperationException RevalidationRejected(LocalRequestReason reason) =>
        LocalRequestDiagnostics.Mark(new InvalidOperationException("Reserved catalog inspection rejected its exact evidence guard; no authority or state was changed."), reason);

    private static async Task RequireNoReservedOutcomeAsync(SqliteConnection connection, SqliteTransaction transaction, object?[] row, CancellationToken token)
    {
        using var outcome = ChirpArticleSql.Command(connection, transaction,
            "SELECT COUNT(*) FROM google_generation_queue_states WHERE AccountId=$account AND OperationStableId=$operation AND IdempotencyKey=$key AND (OutcomeObservedAtUnixMilliseconds IS NOT NULL OR ErrorStatus<>0);",
            ("$account", row[5]), ("$operation", GoogleGenerationProvider.SynthesizeOperationStableId), ("$key", row[7]));
        using var accepted = ChirpArticleSql.Command(connection, transaction,
            "SELECT (SELECT COUNT(*) FROM chirp_article_receipts WHERE AuthorizationId=$id)+(SELECT COUNT(*) FROM chirp_article_output_journals WHERE AuthorizationId=$id);", ("$id", row[0]));
        if (Convert.ToInt64(await outcome.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0
            || Convert.ToInt64(await accepted.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw RevalidationRejected(LocalRequestReason.ReservedRequestIneligible);
    }
}
