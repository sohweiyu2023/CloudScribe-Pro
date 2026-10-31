using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Explicit one-segment-at-a-time durable approval. No provider or credential dependency and no automatic retry/batch loop.</summary>
public sealed partial class ChirpArticleService(IDbContextFactory<CloudScribeDbContext> factory,
    IChirpArticleAdmissionStore admission, AppPaths paths, TimeProvider clock, IChirpReservationResumeAdmissionStore? resumeAdmission = null,
    CompletionWorkflowGate? completionGate = null)
{
    public const string KeyPrefix = "chirp-article:";
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();
    private readonly ChirpArticleEvidenceResolver _evidence = new(paths);
    internal static string Key(string plan, int ordinal) => KeyPrefix + plan + ":" + ordinal.ToString(CultureInfo.InvariantCulture);

    public void Discard(Guid previewId) { _pending.TryRemove(previewId, out _); _resumePending.TryRemove(previewId, out _); }

    public async Task<ChirpRequestReview> PrepareAsync(ChirpRequestIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        intent = intent with { CompiledPayload = intent.CompiledPayload.ToArray() };
        var policy = await PolicyAsync(cancellationToken).ConfigureAwait(false);
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = await CurrentAsync(connection, transaction, policy, intent, cancellationToken).ConfigureAwait(false);
        var existing = await FindExistingAsync(connection, transaction, policy, current.Article.Proposal.PlanSha256, intent.CoverageOrdinal, cancellationToken).ConfigureAwait(false);
        var financial = await ReadFinancialAsync(connection, transaction, policy, cancellationToken).ConfigureAwait(false);
        await RequireReadyAsync(connection, transaction, financial, existing?.AuthorizationId, cancellationToken).ConfigureAwait(false);
        long maximum = current.Article.AllowedSegments.Single(x => x.CoverageOrdinal == intent.CoverageOrdinal).MaximumMinorUnits;
        if (existing is null) RequireCapacity(policy, financial, maximum);
        else await RequireExistingAsync(connection, transaction, policy, intent, existing.AuthorizationId, financial, cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid();
        string reviewHash = ChirpPolicyValidation.Hash(new
        {
            Kind = "chirp-article-segment-review-v1",
            Policy = ChirpPolicyValidation.Hash(policy),
            current.Article,
            intent.Envelope,
            intent.EstimateMinorUnits,
            maximum,
            financial.Sha256,
            Existing = existing?.AuthorizationId
        });
        var review = new ChirpRequestReview(id, reviewHash, current.Article.Proposal.PlanSha256, intent.CoverageOrdinal,
            current.Document.Id, current.Document.CurrentRevisionId!.Value, intent.Envelope.CompiledPayloadSha256, intent.Envelope.CompiledPayloadBytes,
            intent.Currency, intent.Scale, intent.EstimateMinorUnits, maximum, financial.ProjectCommitted, financial.AllocationRemaining,
            existing?.AuthorizationId, policy.ExpiresAtUtc, intent.Envelope.PricingProvenanceId, intent.Envelope.VoiceName, policy.SuccessorGrant?.ParentAuthorizationId);
        foreach (var item in _pending)
            if (clock.GetUtcNow() - item.Value.CreatedAt > TimeSpan.FromMinutes(10)) _pending.TryRemove(item.Key, out _);
        if (_pending.Count >= 32 || !_pending.TryAdd(id, new(intent, review, ChirpPolicyValidation.Hash(policy), financial.Sha256, clock.GetUtcNow())))
            throw new InvalidOperationException("Close earlier article reviews before preparing another request.");
        return review;
    }

    private async Task<ChirpArticleAdmissionPolicy> PolicyAsync(CancellationToken token)
    {
        var value = await admission.LoadVerifiedAsync(token).ConfigureAwait(false)
            ?? throw LocalRequestDiagnostics.Mark(new InvalidOperationException("No current independently reviewed article-plan policy is admitted."), LocalRequestReason.AdmissionChanged);
        var owned = ChirpPolicyFiles.Parse<ChirpArticleAdmissionPolicy>(JsonSerializer.SerializeToUtf8Bytes(value));
        try { ChirpPolicyValidation.RequireValid(owned, clock.GetUtcNow()); }
        catch (InvalidDataException error)
        { throw LocalRequestDiagnostics.Mark(error, LocalRequestReason.AdmissionChanged, admissionExpiresUtc: owned.ExpiresAtUtc); }
        return owned;
    }

    private async Task RequirePolicyCurrentAsync(ChirpArticleAdmissionPolicy expected, CancellationToken token)
    {
        var current = await PolicyAsync(token).ConfigureAwait(false);
        if (!string.Equals(ChirpPolicyValidation.Hash(current), ChirpPolicyValidation.Hash(expected), StringComparison.Ordinal))
            throw new InvalidOperationException("Independent article admission was revoked or replaced during the operation.");
    }

    private async Task<ChirpArticleCurrentEvidence> CurrentAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, ChirpRequestIntent intent, CancellationToken token)
    {
        if (!string.Equals(intent.Envelope.AccountId, policy.AccountId, StringComparison.Ordinal)
            || !string.Equals(intent.ProjectId, policy.ProjectId, StringComparison.Ordinal) || !string.Equals(intent.Currency, policy.Currency, StringComparison.Ordinal)
            || intent.Scale != policy.Scale || intent.EstimateMinorUnits < 0 || intent.Envelope.RecoveryAuthorizationId is not null
            || intent.Envelope.ChirpAuthorizationId is not null || intent.CompiledPayload.Length is < 1 or > 5_000
            || !string.Equals(ChirpPolicyFiles.Hash(intent.CompiledPayload.Span), intent.Envelope.CompiledPayloadSha256, StringComparison.Ordinal)
            || intent.CompiledPayload.Length != intent.Envelope.CompiledPayloadBytes)
            throw new InvalidOperationException("The current article request does not match independently admitted account/project/compiled evidence.");
        var financial = await ReadFinancialAsync(connection, transaction, policy, token).ConfigureAwait(false);
        await RequireSuccessorParentAsync(connection, transaction, policy, financial, token).ConfigureAwait(false);
        if (policy.SuccessorGrant is { } successor && !string.Equals(intent.Envelope.CredentialReferenceId, successor.CredentialReferenceId, StringComparison.Ordinal))
            throw new InvalidOperationException("The successor's independently admitted credential binding changed.");
        var current = await _evidence.ResolveAsync(connection, transaction, policy, intent.DocumentId, financial.Old, token).ConfigureAwait(false);
        var part = current.Article.Proposal.Parts.SingleOrDefault(x => x.CoverageOrdinal == intent.CoverageOrdinal);
        var allowance = current.Article.AllowedSegments.SingleOrDefault(x => x.CoverageOrdinal == intent.CoverageOrdinal);
        if (part is null || allowance is null || part.Range.ReusedReceiptSha256 is not null || intent.EstimateMinorUnits > allowance.MaximumMinorUnits
            || !string.Equals(intent.Envelope.CompiledPayloadSha256, part.PayloadSha256, StringComparison.Ordinal) || intent.Envelope.CompiledPayloadBytes != part.PayloadBytes
            || !string.Equals(intent.Envelope.VoiceName, current.Article.Proposal.VoiceName, StringComparison.Ordinal)
            || !string.Equals(intent.Envelope.AudioEncoding, "MP3", StringComparison.Ordinal))
            throw new InvalidOperationException("Exact internal segment, voice, current estimate or approved request limit changed.");
        return new(current.Document, current.Article, financial.Sha256, financial.ProjectCommitted, financial.AllocationRemaining);
    }

    private sealed record Pending(ChirpRequestIntent Intent, ChirpRequestReview Review, string PolicySha256, string FinancialSha256, DateTimeOffset CreatedAt);
}
