using CloudScribe.Infrastructure.Files;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private async Task<NarrationWholeReviewScope> PrepareNarrationWholeReviewAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, AdmittedChirpArticle article, ChirpPartProgress[] remaining,
        CurrentFinancial financial, ChirpWholeArticleJob? existing, CancellationToken token)
    {
        var ordinary = remaining.Where(x => string.Equals(x.Status, "Not requested", StringComparison.Ordinal)).ToArray();
        var failed = remaining.Where(x => !string.Equals(x.Status, "Not requested", StringComparison.Ordinal)).ToArray();
        foreach (var part in failed)
            _ = await RecoverableOrdinalAsync(connection, transaction, policy, article.Proposal.PlanSha256, part.CoverageOrdinal, token).ConfigureAwait(false);
        await _evidence.ResolveAsync(connection, transaction, policy, article.Proposal.DocumentId, financial.Old, token).ConfigureAwait(false);
        await RequireWholeReviewReadyAsync(connection, transaction, policy, financial, existing, token).ConfigureAwait(false);
        bool recoveryOnly = existing?.NarrationRecoveryOnly == true;
        if (ordinary.Length > 0 && !recoveryOnly)
        {
            try { await RequireCurrentNarrationCopyAsync(article.Proposal.DocumentId, token).ConfigureAwait(false); }
            catch (InvalidOperationException) when (existing is not null && failed.Length > 0)
            {
                // Only changed/inactive source provenance can narrow an already recoverable job.
                // Child DB evidence and its durable blob are independently verified below.
                recoveryOnly = true;
            }
        }
        if (ordinary.Length > 0 && recoveryOnly)
        {
            await RequireNarrationRecoveryCheckpointAsync(connection, transaction, policy, article, existing, token).ConfigureAwait(false);
            if (!failed.Any(x => x.CoverageOrdinal == existing!.ActiveOrdinal))
                throw new InvalidOperationException("The retained active range is not eligible for exact recovery.");
            return new(failed, 0, failed.Length, false, ordinary.Length, existing);
        }
        return new(remaining, ordinary.Sum(x => article.AllowedSegments.Single(a => a.CoverageOrdinal == x.CoverageOrdinal).MaximumMinorUnits),
            failed.Length, ordinary.Length > 0, 0, null);
    }

    private async Task RequireNarrationRecoveryCheckpointAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, AdmittedChirpArticle article, ChirpWholeArticleJob? existing, CancellationToken token)
    {
        if (existing is null || existing.DocumentId != article.Proposal.DocumentId || existing.ActiveOrdinal is not { } ordinal
            || existing.State is not (ChirpWholeArticleState.Executing or ChirpWholeArticleState.Stopped)
            || existing.MaximumRecoveryRequests < 2 || !string.Equals(existing.PlanSha256, article.Proposal.PlanSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Only the exact existing failed-range job may continue in recovery-only mode.");
        _ = await RecoverableOrdinalAsync(connection, transaction, policy, existing.PlanSha256, ordinal, token).ConfigureAwait(false);
        await VerifyNarrationRecoveryChildAsync(connection, transaction, article.Proposal, token).ConfigureAwait(false);
    }

    private async Task VerifyNarrationRecoveryChildAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticlePlanProposal proposal, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction, """
            SELECT c.ChildRevisionId,c.ChildTextSha256,r.ContentRelativePath,r.ContentByteLength
            FROM narration_copies c JOIN document_revisions r ON r.Id=c.ChildRevisionId AND r.DocumentId=c.ChildDocumentId
            WHERE lower(c.ChildDocumentId)=$document;
            """, ("$document", proposal.DocumentId.ToString("D")));
        string path; long bytes;
        using (var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(token).ConfigureAwait(false) || Guid.Parse(row.GetString(0)) != proposal.RevisionId
                || !string.Equals(row.GetString(1), proposal.SourceSha256, StringComparison.Ordinal) || row.IsDBNull(2) || row.IsDBNull(3))
                throw new InvalidDataException("The existing recovery job lacks its exact durable narration-copy pin.");
            path = row.GetString(2); bytes = row.GetInt64(3);
        }
        _ = await new DocumentContentStore(paths).ReadVerifiedAsync(new(path, proposal.SourceSha256, bytes), token).ConfigureAwait(false);
    }

    private async Task<ChirpWholeArticleJob?> TryStartNarrationRecoveryOnlyAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, CurrentFinancial financial, ChirpWholeArticleJob? existing, WholePending pending, CancellationToken token)
    {
        if (pending.Review.BlockedOrdinaryRequests == 0) return null;
        if (existing is null || pending.RecoveryJob is null || existing != pending.RecoveryJob)
            throw new InvalidOperationException("The exact recovery-only checkpoint changed; no replacement job is permitted.");
        var current = await _evidence.ResolveAsync(connection, transaction, policy, pending.Review.DocumentId, financial.Old, token).ConfigureAwait(false);
        await RequireWholeReviewReadyAsync(connection, transaction, policy, financial, existing, token).ConfigureAwait(false);
        await RequireNarrationRecoveryCheckpointAsync(connection, transaction, policy, current.Article, existing, token).ConfigureAwait(false);
        if (existing.State == ChirpWholeArticleState.Stopped || !string.Equals(existing.PolicySha256, ChirpPolicyValidation.Hash(policy), StringComparison.Ordinal))
            return await ContinueReviewedWholeJobAsync(connection, transaction, policy, financial, existing, pending.Review, token).ConfigureAwait(false);
        var narrowed = existing with
        {
            ReviewSha256 = pending.Review.ReviewSha256,
            NarrationRecoveryOnly = true,
            LastObservedAtUtc = clock.GetUtcNow()
        };
        await WriteWholeArticleJobAsync(connection, transaction, narrowed, false, token).ConfigureAwait(false);
        await RequirePolicyCurrentAsync(policy, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); transaction.Commit();
        return narrowed;
    }

    private sealed record NarrationWholeReviewScope(ChirpPartProgress[] Remaining, long OrdinaryMaximum, int Failed,
        bool RequiresCurrentNarrationCopy, int BlockedOrdinaryRequests, ChirpWholeArticleJob? RecoveryJob);
}
