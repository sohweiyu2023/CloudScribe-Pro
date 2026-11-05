using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private readonly ConcurrentDictionary<Guid, WholePending> _wholePending = new();
    private readonly System.Threading.Lock _wholeReviewGate = new();

    public async Task<ChirpWholeArticleReview> PrepareWholeArticleAsync(Guid documentId, CancellationToken token = default)
    {
        var policy = await PolicyAsync(token).ConfigureAwait(false);
        var article = policy.Articles.Single(x => x.Proposal.DocumentId == documentId);
        RequireWholeArticleGrant(policy, article.Proposal.PlanSha256);
        var progress = await ProgressAsync(documentId, token).ConfigureAwait(false);
        if (!string.Equals(progress.PlanSha256, article.Proposal.PlanSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The saved coverage and admitted article plan differ.");
        var remaining = progress.Parts.Where(x => !x.Reused && !string.Equals(x.Status, "Saved accepted audio", StringComparison.Ordinal)).ToArray();
        if (remaining.Any(x => !x.CurrentlyAdmitted || policy.SentenceRecovery is null && !string.Equals(x.Status, "Not requested", StringComparison.Ordinal)))
            throw new InvalidOperationException("Reconcile the existing request or recover its local output before starting an article job. No automatic retry is permitted.");
        long maximum = remaining.Sum(x => article.AllowedSegments.Single(a => a.CoverageOrdinal == x.CoverageOrdinal).MaximumMinorUnits);
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var financial = await ReadFinancialAsync(connection, transaction, policy, token).ConfigureAwait(false);
        var existingJob = await FindReviewedWholeJobAsync(connection, transaction, policy, article.Proposal.PlanSha256, token).ConfigureAwait(false);
        int failed = 0;
        foreach (var part in remaining.Where(x => !string.Equals(x.Status, "Not requested", StringComparison.Ordinal)))
        {
            _ = await RecoverableOrdinalAsync(connection, transaction, policy, article.Proposal.PlanSha256, part.CoverageOrdinal, token).ConfigureAwait(false);
            maximum -= article.AllowedSegments.Single(x => x.CoverageOrdinal == part.CoverageOrdinal).MaximumMinorUnits; failed++;
        }
        await _evidence.ResolveAsync(connection, transaction, policy, documentId, financial.Old, token).ConfigureAwait(false);
        await RequireWholeReviewReadyAsync(connection, transaction, policy, financial, existingJob, token).ConfigureAwait(false);
        if (remaining.Length - failed > policy.MaximumNewAttempts - financial.Extra.Length
            || maximum > policy.MaximumAdditionalReservationMinorUnits - financial.Additional
            || maximum > financial.AllocationRemaining || maximum > policy.Budget.ProjectCeilingMinorUnits - financial.ProjectCommitted)
            throw new InvalidOperationException("The complete article exceeds the retained budget or independently admitted request count.");
        var recovery = remaining.Length == 0 ? (Requests: 0, MinorUnits: 0L)
            : RecoveryReviewAllowance(policy, financial, article.Proposal.PlanSha256, remaining.Length - failed, maximum);
        maximum = checked(maximum + recovery.MinorUnits);
        if (failed > 0 && recovery.Requests == 0) throw new InvalidOperationException("The reviewed recovery allowance cannot cover the existing failed range.");
        var now = clock.GetUtcNow();
        string hash = ChirpPolicyValidation.Hash(new
        {
            Kind = "whole-article-review-v1",
            Policy = ChirpPolicyValidation.Hash(policy),
            article,
            financial.Sha256,
            Ordinals = remaining.Select(x => x.CoverageOrdinal).ToArray(),
            Maximum = maximum,
            RecoveryRequests = recovery.Requests,
            RecoveryMinorUnits = recovery.MinorUnits
        });
        var review = new ChirpWholeArticleReview(Guid.NewGuid(), hash, documentId, article.Proposal.PlanSha256,
            article.Proposal.VoiceName, policy.Currency, policy.Scale, progress.SavedCoverageParts, progress.TotalCoverageParts,
            remaining.Length, maximum, financial.ProjectCommitted, policy.ExpiresAtUtc, policy.WholeArticleGeneration!.MaximumCatalogRefreshes,
            policy.ExpiresAtUtc, recovery.Requests, recovery.MinorUnits, ChirpPolicyValidation.IsCompletionBound(policy));
        await RequirePolicyCurrentAsync(policy, token).ConfigureAwait(false);
        PublishWholeReview(review, ChirpPolicyValidation.Hash(policy), financial.Sha256, now);
        return review;
    }

    private void PublishWholeReview(ChirpWholeArticleReview review, string policy, string financial, DateTimeOffset now)
    {
        lock (_wholeReviewGate)
        {
            foreach (var item in _wholePending)
                if (!item.Value.Review.CompletionBound && now >= item.Value.Review.ConfirmByUtc || item.Value.Review.DocumentId == review.DocumentId
                    || !string.Equals(item.Value.PolicySha256, policy, StringComparison.Ordinal)) _wholePending.TryRemove(item.Key, out _);
            _wholePending[review.PreviewId] = new(review, policy, financial, now);
        }
    }

    public async Task<ChirpWholeArticleJob> StartWholeArticleAsync(ChirpWholeArticleConfirmation confirmation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        if (!confirmation.ConfirmedByUser || !confirmation.AcknowledgedHistoricalHolds
            || !_wholePending.TryRemove(confirmation.PreviewId, out var pending)
            || !string.Equals(confirmation.ReviewSha256, pending.Review.ReviewSha256, StringComparison.Ordinal)
            || confirmation.MaximumAdditionalMinorUnits != pending.Review.MaximumAdditionalMinorUnits
            || clock.GetUtcNow() < pending.CreatedAt || !pending.Review.CompletionBound && clock.GetUtcNow() >= pending.Review.ConfirmByUtc)
            throw new InvalidOperationException("Review the complete article, maximum cost and retained holds before Generate.");
        var policy = await PolicyAsync(token).ConfigureAwait(false);
        RequireWholeArticleGrant(policy, pending.Review.PlanSha256);
        if (!string.Equals(ChirpPolicyValidation.Hash(policy), pending.PolicySha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The independent article workflow admission changed after review.");
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await FindReviewedWholeJobAsync(connection, transaction, policy, pending.Review.PlanSha256, token).ConfigureAwait(false);
        var financial = await ReadFinancialAsync(connection, transaction, policy, token).ConfigureAwait(false);
        if (!string.Equals(financial.Sha256, pending.FinancialSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Retained financial evidence changed. Review the article again.");
        await _evidence.ResolveAsync(connection, transaction, policy, pending.Review.DocumentId, financial.Old, token).ConfigureAwait(false);
        await RequireWholeReviewReadyAsync(connection, transaction, policy, financial, existing, token).ConfigureAwait(false);
        if (existing?.State == ChirpWholeArticleState.Complete) return existing;
        if (existing is not null && (existing.State == ChirpWholeArticleState.Stopped
            || !string.Equals(existing.PolicySha256, ChirpPolicyValidation.Hash(policy), StringComparison.Ordinal)))
            return await ContinueReviewedWholeJobAsync(connection, transaction, policy, financial, existing, pending.Review, token).ConfigureAwait(false);
        if (existing is not null) return existing; // The same reviewed job, never a new authority or deadline.
        bool completionBound = ChirpPolicyValidation.IsCompletionBound(policy);
        var job = new ChirpWholeArticleJob(Guid.NewGuid(), pending.Review.DocumentId, pending.Review.PlanSha256,
            pending.PolicySha256, pending.Review.ReviewSha256,
            completionBound && policy.SentenceRecovery is not null ? policy.Budget.ProjectCeilingMinorUnits : confirmation.MaximumAdditionalMinorUnits,
            pending.Review.ExpiresAtUtc, clock.GetUtcNow(), ChirpWholeArticleState.Ready, null, "article.ready",
            MaximumRecoveryRequests: completionBound ? policy.SentenceRecovery?.MaximumChildAttemptsPerJob ?? 0 : pending.Review.MaximumRecoveryRequests,
            MaximumRecoveryMinorUnits: completionBound ? policy.SentenceRecovery?.MaximumAdditionalMinorUnits ?? 0 : pending.Review.MaximumRecoveryMinorUnits,
            CompletionBound: completionBound);
        await WriteWholeArticleJobAsync(connection, transaction, job, true, token).ConfigureAwait(false);
        await RequirePolicyCurrentAsync(policy, token).ConfigureAwait(false);
        transaction.Commit();
        return job;
    }

    public async Task<ChirpWholeArticleJob> ReadWholeArticleJobAsync(Guid jobId, CancellationToken token = default)
    {
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        return await ReadWholeArticleJobAsync(connection, transaction, jobId, token).ConfigureAwait(false);
    }

    public async Task<ChirpApprovedAttempt> ApproveWholeArticleRequestAsync(Guid jobId, Guid previewId, CancellationToken token)
    {
        if (!_pending.TryRemove(previewId, out var pending) || clock.GetUtcNow() < pending.CreatedAt
            || clock.GetUtcNow() - pending.CreatedAt > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("The exact internal compilation expired. No request was reserved.");
        return await ApproveCoreAsync(pending, pending.Review.MaximumMinorUnits, jobId, token).ConfigureAwait(false);
    }

    private static void RequireWholeArticleGrant(ChirpArticleAdmissionPolicy policy, string plan)
    {
        if (policy.SchemaVersion != 3 || policy.WholeArticleGeneration is not { } grant
            || !grant.PlanSha256s.Contains(plan, StringComparer.Ordinal))
            throw new InvalidOperationException("No independently admitted whole-article workflow exists for this plan. Request-level approval cannot authorize it.");
    }

    private async Task RequireWholeArticleRuntimeJobAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, string plan, int ordinal, CancellationToken token)
    {
        RequireWholeArticleGrant(policy, plan);
        var job = await FindWholeArticleJobAsync(connection, transaction, ChirpPolicyValidation.Hash(policy), plan, token).ConfigureAwait(false);
        if (job is null || job.State != ChirpWholeArticleState.Executing || job.ActiveOrdinal != ordinal
            || job.CompletionBound != ChirpPolicyValidation.IsCompletionBound(policy)
            || clock.GetUtcNow() < job.LastObservedAtUtc || ChirpPolicyValidation.IsJobExpired(job, clock.GetUtcNow()))
            throw new InvalidOperationException("The article job is stopped, expired or no longer owns this request. No dispatch is permitted.");
    }

    private async Task RequireWholeArticleJobAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, Guid jobId, string plan, int ordinal, CancellationToken token)
    {
        RequireWholeArticleGrant(policy, plan);
        var job = await ReadWholeArticleJobAsync(connection, transaction, jobId, token).ConfigureAwait(false);
        if (job.State != ChirpWholeArticleState.Executing || job.ActiveOrdinal != ordinal || ChirpPolicyValidation.IsJobExpired(job, clock.GetUtcNow())
            || job.CompletionBound != ChirpPolicyValidation.IsCompletionBound(policy)
            || clock.GetUtcNow() < job.LastObservedAtUtc
            || !string.Equals(job.PolicySha256, ChirpPolicyValidation.Hash(policy), StringComparison.Ordinal)
            || !string.Equals(job.PlanSha256, plan, StringComparison.Ordinal))
            throw new InvalidOperationException("The durable article consent, exact active request or admission is not current.");
        using var spent = ChirpArticleSql.Command(connection, transaction,
            "SELECT COALESCE(SUM(ReservedMinorUnits),0) FROM chirp_article_owned_attempts WHERE PolicySha256=$policy AND PlanSha256=$plan;",
            ("$policy", job.PolicySha256), ("$plan", plan));
        long retained = Convert.ToInt64(await spent.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        var article = policy.Articles.Single(x => string.Equals(x.Proposal.PlanSha256, plan, StringComparison.Ordinal));
        long maximum = article.AllowedSegments.Single(x => x.CoverageOrdinal == ordinal).MaximumMinorUnits;
        if (maximum > job.MaximumAdditionalMinorUnits - retained)
            throw new InvalidOperationException("The article-level cost consent is exhausted; all holds remain retained.");
    }

    private sealed record WholePending(ChirpWholeArticleReview Review, string PolicySha256, string FinancialSha256, DateTimeOffset CreatedAt);
}
