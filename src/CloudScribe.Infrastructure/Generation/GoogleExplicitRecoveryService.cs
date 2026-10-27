using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Opt-in recovery boundary. No provider, credential or automatic approval dependency.</summary>
public sealed partial class GoogleExplicitRecoveryService(
    IDbContextFactory<CloudScribeDbContext> factory, TimeProvider timeProvider, IGoogleRecoveryBudgetEvidenceResolver budgetEvidence,
    IGoogleRecoveryPredecessorEvidenceResolver predecessorEvidence)
{
    public const string KeyPrefix = "recovery-google-tts:";
    private readonly ConcurrentDictionary<Guid, PendingReview> _reviews = new();

    public async Task<GoogleRecoveryPreview> PrepareAsync(GoogleRecoveryIntent intent,
        long additionalMaximumMinorUnits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        intent = intent with { CompiledPayload = intent.CompiledPayload.ToArray() };
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        string financial = await FinancialDigestAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var budget = await ResolveBudgetAsync(intent.Envelope.AccountId, intent.ProjectId, intent.Currency, intent.Scale, financial, cancellationToken).ConfigureAwait(false);
        RequireIntent(intent, budget, additionalMaximumMinorUnits);
        var previous = await LoadPreviousAsync(connection, transaction, intent, cancellationToken).ConfigureAwait(false);
        var association = await ResolveLogicalIdentityAsync(connection, transaction, intent, previous, cancellationToken).ConfigureAwait(false);
        string logical = association.LogicalNarrationId;
        await RequireHeadAndBudgetAsync(connection, transaction, intent, budget, additionalMaximumMinorUnits, logical, cancellationToken).ConfigureAwait(false);
        string digest = Digest(new { intent, previous, budget, additionalMaximumMinorUnits, financial, association });
        var id = Guid.NewGuid();
        var preview = new GoogleRecoveryPreview(id, digest, logical, intent, previous, budget, additionalMaximumMinorUnits, financial, association);
        foreach (var item in _reviews)
            if (timeProvider.GetUtcNow() - item.Value.CreatedAt > TimeSpan.FromMinutes(10)) _reviews.TryRemove(item.Key, out _);
        var privatePreview = preview with { Intent = intent with { CompiledPayload = intent.CompiledPayload.ToArray() } };
        if (_reviews.Count >= 32 || !_reviews.TryAdd(id, new(privatePreview, timeProvider.GetUtcNow())))
            throw new InvalidOperationException("Close earlier recovery reviews before preparing another.");
        return preview;
    }

    public async Task<GoogleRecoveryApprovedAttempt> ApproveAsync(GoogleRecoveryApprovalConfirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        if (!confirmation.ConfirmedByUser || !confirmation.AcknowledgedHistoricalHolds)
            throw new InvalidOperationException("An additional attempt requires explicit approval and acknowledgement that prior holds may still be charged.");
        if (!_reviews.TryRemove(confirmation.PreviewId, out var pending)
            || timeProvider.GetUtcNow() - pending.CreatedAt > TimeSpan.FromMinutes(10)
            || !string.Equals(confirmation.DisplayedReviewSha256, pending.Preview.ReviewSha256
, StringComparison.Ordinal) || confirmation.AdditionalMaximumMinorUnits != pending.Preview.AdditionalMaximumMinorUnits)
            throw new InvalidOperationException("Exact recovery approval is missing, changed or stale.");
        var p = pending.Preview;
        RequireIntent(p.Intent, p.Budget, p.AdditionalMaximumMinorUnits);
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var currentBudget = await ResolveBudgetAsync(p.Intent.Envelope.AccountId, p.Intent.ProjectId, p.Intent.Currency, p.Intent.Scale,
            p.FinancialStateSha256, cancellationToken).ConfigureAwait(false);
        if (currentBudget != p.Budget) throw new InvalidOperationException("Approved budget evidence changed; prepare a new review.");
        if (!string.Equals(await FinancialDigestAsync(connection, transaction, cancellationToken).ConfigureAwait(false), p.FinancialStateSha256
, StringComparison.Ordinal) || await LoadPreviousAsync(connection, transaction, p.Intent, cancellationToken).ConfigureAwait(false) != p.PreviousState)
            throw new InvalidOperationException("Financial or predecessor evidence changed; prepare a fresh review.");
        if (await ResolveLogicalIdentityAsync(connection, transaction, p.Intent, p.PreviousState, cancellationToken).ConfigureAwait(false) != p.AssociationProof)
            throw new InvalidOperationException("Independent predecessor association changed.");
        await RequireHeadAndBudgetAsync(connection, transaction, p.Intent, p.Budget, p.AdditionalMaximumMinorUnits, p.LogicalItemId, cancellationToken).ConfigureAwait(false);
        Guid authorizationId = Guid.NewGuid();
        string childKey = KeyPrefix + authorizationId.ToString("N");
        await AppendApprovalAsync(connection, transaction, p, authorizationId, childKey, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return new(authorizationId, childKey, p.ReviewSha256, p.AdditionalMaximumMinorUnits);
    }

    private static void RequireIntent(GoogleRecoveryIntent intent, GoogleRecoveryBudgetReview budget, long maximum)
    {
        if (intent.DocumentId == Guid.Empty || intent.RevisionId == Guid.Empty || string.IsNullOrWhiteSpace(intent.ProjectId)
            || string.IsNullOrWhiteSpace(intent.PreviousIdempotencyKey) || intent.Scale is < 0 or > 9
            || intent.Currency.Length != 3 || intent.EstimateMinorUnits < 0 || maximum < intent.EstimateMinorUnits
            || budget.RetainedCommittedMinorUnits < 0 || budget.RemainingAllocationMinorUnits < maximum
            || checked(budget.RetainedCommittedMinorUnits + maximum) > budget.ProjectCeilingMinorUnits
            || budget.EvidenceSha256.Length != 64 || budget.EvidenceSha256.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidOperationException("Recovery requires exact identity and independently reviewed conservative budget bounds.");
        if (intent.Envelope.CompiledPayloadBytes is < 1 or > 5000 || intent.Envelope.CompiledPayloadSha256.Length != 64)
            throw new InvalidOperationException("Recovery requires the exact compiled request envelope.");
        if (intent.CompiledPayload.Length != intent.Envelope.CompiledPayloadBytes
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(intent.CompiledPayload.Span)), intent.Envelope.CompiledPayloadSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery compiled bytes do not match the exact envelope.");
    }

    private static string Digest<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private async Task<GoogleRecoveryBudgetReview> ResolveBudgetAsync(string account, string project, string currency, int scale,
        string financial, CancellationToken cancellationToken)
    {
        var evidence = await budgetEvidence.ResolveAsync(account, project, currency, scale, financial, cancellationToken).ConfigureAwait(false);
        if (evidence is null || !string.Equals(evidence.AccountId, account, StringComparison.Ordinal)
            || !string.Equals(evidence.ProjectId, project, StringComparison.Ordinal) || !string.Equals(evidence.Currency, currency, StringComparison.Ordinal)
            || evidence.Scale != scale || !string.Equals(evidence.FinancialStateSha256, financial, StringComparison.Ordinal)
            || evidence.ExpiresAtUtc <= timeProvider.GetUtcNow())
            throw new InvalidOperationException("Authoritative recovery budget evidence is missing, stale or mismatched.");
        return evidence.ApprovedBudget;
    }
    private sealed record PendingReview(GoogleRecoveryPreview Preview, DateTimeOffset CreatedAt);
}
