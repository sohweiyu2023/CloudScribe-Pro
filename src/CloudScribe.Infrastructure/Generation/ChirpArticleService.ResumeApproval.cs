using System.Collections.Concurrent;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private readonly ConcurrentDictionary<Guid, ResumePending> _resumePending = new();

    public async Task<ChirpResolvedSegment?> ResolveReservedResumeSegmentAsync(Guid document, int ordinal, string account,
        string project, string voice, string language, CancellationToken token = default)
    {
        var policy = await ResumePolicyAsync(null, token).ConfigureAwait(false);
        if (policy is null) return null;
        var article = policy.HistoricalPolicy.Articles.SingleOrDefault(x => x.Proposal.DocumentId == document
            && x.Proposal.Parts.Any(p => p.CoverageOrdinal == ordinal && string.Equals(p.PayloadSha256, policy.OriginalEnvelope.CompiledPayloadSha256, StringComparison.Ordinal)));
        if (article is null) return null;
        if (!string.Equals(account, policy.OriginalEnvelope.AccountId, StringComparison.Ordinal) || !string.Equals(project, policy.HistoricalPolicy.ProjectId, StringComparison.Ordinal)
            || !string.Equals(voice, policy.ExecutionEnvelope.VoiceName, StringComparison.Ordinal) || !string.Equals(language, article.Proposal.LanguageCode, StringComparison.Ordinal)) throw ResumeRejected();
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var current = await ResolveResumeAsync(connection, transaction, policy, policy.ExecutionEnvelope, false, token).ConfigureAwait(false);
        await RequireResumeCurrentAsync(policy, token).ConfigureAwait(false);
        return new(current.Document, current.Proposal, current.Part, ChirpNarrationPlanner.Slice(current.Document.Text, current.Part.Range));
    }

    public async Task<ChirpRequestReview> PrepareReservedResumeAsync(ChirpRequestIntent intent, string selectionSha256,
        string runtimeEvidenceSha256, Guid catalogId, string requestIdentitySha256, CancellationToken token = default)
    {
        var policy = await ResumePolicyAsync(null, token).ConfigureAwait(false) ?? throw ResumeRejected();
        if ((intent.Envelope with { ChirpAuthorizationId = policy.AuthorizationId }) != policy.ExecutionEnvelope
            || catalogId != policy.CatalogSnapshotId || !string.Equals(requestIdentitySha256, policy.RequestIdentitySha256, StringComparison.Ordinal)
            || !string.Equals(selectionSha256, policy.SelectionSha256, StringComparison.Ordinal)
            || !string.Equals(runtimeEvidenceSha256, policy.RuntimeEvidenceSha256, StringComparison.Ordinal)) throw ResumeRejected();
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var current = await ResolveResumeAsync(connection, transaction, policy, policy.ExecutionEnvelope, false, token).ConfigureAwait(false);
        if (current.Document.Id != intent.DocumentId || current.Part.CoverageOrdinal != intent.CoverageOrdinal
            || !string.Equals(intent.ProjectId, policy.HistoricalPolicy.ProjectId, StringComparison.Ordinal)
            || !string.Equals(intent.Currency, (string)current.Row[10]!, StringComparison.Ordinal) || intent.Scale != (int)(long)current.Row[11]!
            || intent.EstimateMinorUnits != (long)current.Row[12]! || !intent.CompiledPayload.Span.SequenceEqual(current.Payload.Span)) throw ResumeRejected();
        await RequireResumeCurrentAsync(policy, token).ConfigureAwait(false);
        Guid id = Guid.NewGuid();
        var review = new ChirpRequestReview(id, ChirpPolicyValidation.Hash(new { Kind = "reserved-catalog-resume-review-v1", policy }),
            current.Proposal.PlanSha256, current.Part.CoverageOrdinal, current.Document.Id, current.Document.CurrentRevisionId!.Value,
            policy.ExecutionEnvelope.CompiledPayloadSha256, policy.ExecutionEnvelope.CompiledPayloadBytes, intent.Currency, intent.Scale,
            intent.EstimateMinorUnits, (long)current.Row[13]!, current.Financial.ProjectCommitted, current.Financial.AllocationRemaining,
            policy.AuthorizationId, policy.ExpiresAtUtc, policy.ExecutionEnvelope.PricingProvenanceId, policy.ExecutionEnvelope.VoiceName,
            IsCatalogRevalidation: true);
        foreach (var item in _resumePending)
            if (clock.GetUtcNow() - item.Value.CreatedAt > TimeSpan.FromMinutes(10)) _resumePending.TryRemove(item.Key, out _);
        if (_resumePending.Count >= 32 || !_resumePending.TryAdd(id, new(policy, review, clock.GetUtcNow()))) throw ResumeRejected();
        return review;
    }

    public async Task<ChirpApprovedAttempt> ApproveReservedResumeAsync(ChirpApprovalConfirmation confirmation, CancellationToken token = default)
    {
        completionGate?.RequireReady();
        if (!confirmation.ConfirmedByUser || !confirmation.AcknowledgedHistoricalHolds
            || !_resumePending.TryRemove(confirmation.PreviewId, out var pending)) throw ResumeRejected();
        if (clock.GetUtcNow() - pending.CreatedAt > TimeSpan.FromMinutes(10))
            throw LocalRequestDiagnostics.Mark(ResumeRejected(), LocalRequestReason.ReviewExpired, pending.CreatedAt, pending.Policy.ExpiresAtUtc);
        if (!string.Equals(confirmation.DisplayedReviewSha256, pending.Review.ReviewSha256, StringComparison.Ordinal)
            || confirmation.MaximumMinorUnits != pending.Review.MaximumMinorUnits) throw ResumeRejected();
        await RequireResumeCurrentAsync(pending.Policy, token).ConfigureAwait(false);
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ResolveResumeAsync(connection, transaction, pending.Policy, pending.Policy.ExecutionEnvelope, false, token).ConfigureAwait(false);
        string json = JsonSerializer.Serialize(pending.Policy), hash = ChirpPolicyValidation.Hash(pending.Policy);
        using (var insert = ChirpArticleSql.Command(connection, transaction,
            "INSERT INTO chirp_reserved_catalog_mappings VALUES($id,$mapping,$json,$sha,$time,$prior) ON CONFLICT(MappingId) DO NOTHING;",
            ("$id", pending.Policy.AuthorizationId.ToString("N")), ("$mapping", pending.Policy.MappingId.ToString("N")), ("$json", json),
            ("$sha", hash), ("$time", clock.GetUtcNow().ToUnixTimeMilliseconds()), ("$prior", pending.Policy.PriorMappingSha256)))
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await RequireStoredResumeMappingAsync(connection, transaction, pending.Policy, true, token).ConfigureAwait(false);
        await RequireResumeCurrentAsync(pending.Policy, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); transaction.Commit();
        return new(pending.Policy.AuthorizationId, current.Authorized.Key, (long)current.Row[13]!);
    }

    private static async Task RequireNoResumeMappingAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT COUNT(*) FROM chirp_reserved_catalog_mappings WHERE AuthorizationId=$id;", ("$id", id.ToString("N")));
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0) throw ResumeRejected();
    }

    private sealed record ResumePending(ChirpReservationResumePolicy Policy, ChirpRequestReview Review, DateTimeOffset CreatedAt);
}
