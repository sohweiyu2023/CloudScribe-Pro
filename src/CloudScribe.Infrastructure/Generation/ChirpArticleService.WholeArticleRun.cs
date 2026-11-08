namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    public async Task<ChirpWholeArticleJob> RunWholeArticleAsync(Guid jobId, IChirpWholeArticleRuntime runtime,
        IProgress<ChirpArticleProgress>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        // The OS releases this lock after a crash. A persisted Executing checkpoint still forbids blind replay.
        using var worker = new FileStream(Path.Combine(paths.RootDirectory, ".whole-article-worker.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var job = await ReadWholeArticleJobAsync(jobId, token).ConfigureAwait(false);
        if (job.State == ChirpWholeArticleState.Stopped) return job;
        try
        {
            if (clock.GetUtcNow() < job.LastObservedAtUtc) throw new InvalidOperationException("Article job clock rollback.");
            if (job.State == ChirpWholeArticleState.Complete)
            {
                await VerifyWholeArticleOutputAsync(job.Output, token).ConfigureAwait(false);
                return job;
            }
            RequireWholeJobLifetime(job);
            if (job.State == ChirpWholeArticleState.Assembling)
                return await TransitionWholeArticleAsync(job, ChirpWholeArticleState.Stopped, null, "article.export-interrupted-reconcile-local-output", token).ConfigureAwait(false);
            if (job.State == ChirpWholeArticleState.RefreshingCatalog)
                return await TransitionWholeArticleAsync(job, ChirpWholeArticleState.Stopped, null, "article.refresh-interrupted-reconcile", token).ConfigureAwait(false);
            var coverage = await ProgressAsync(job.DocumentId, token).ConfigureAwait(false);
            if (job.State == ChirpWholeArticleState.Executing)
            {
                if (job.MaximumRecoveryRequests < 2 && !IsAccepted(coverage.Parts.Single(x => x.CoverageOrdinal == job.ActiveOrdinal)))
                    return await TransitionWholeArticleAsync(job, ChirpWholeArticleState.Stopped, job.ActiveOrdinal, "article.interrupted-reconcile-no-retry", token).ConfigureAwait(false);
                coverage = await ReconcileExecutingRecoveryAsync(job, runtime, coverage, token).ConfigureAwait(false);
                job = await TransitionWholeArticleAsync(job, ChirpWholeArticleState.Ready, null, "article.accepted-reconciled", token).ConfigureAwait(false);
            }
            job = await GenerateWholeArticleRemainingAsync(job, runtime, coverage, progress, token).ConfigureAwait(false);
            if (job.State == ChirpWholeArticleState.Stopped) return job;
            // Assembly is local and may finish after paid authority expires. The existing exporter rechecks every receipt and fully decodes.
            job = await TransitionWholeArticleAsync(job, ChirpWholeArticleState.Assembling, null, "article.assembling", token).ConfigureAwait(false);
            var output = await runtime.ExportAsync(job, token).ConfigureAwait(false);
            await VerifyWholeArticleOutputAsync(output, token).ConfigureAwait(false);
            return await TransitionWholeArticleAsync(job, ChirpWholeArticleState.Complete, null, "article.complete", token, output).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            // No raw provider, document, path or exception text is persisted. If this checkpoint fails, Executing remains fail-closed.
            string reason = WholeArticleStopReason(error);
            try
            {
                job = await ReadWholeArticleJobAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                await TransitionWholeArticleAsync(job, ChirpWholeArticleState.Stopped, job.ActiveOrdinal, reason, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception checkpointError) when (checkpointError is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            { /* Preserve the original failure. Durable Executing/Assembling still prevents blind replay. */ }
            throw;
        }
    }
    private string WholeArticleStopReason(Exception error)
    {
        var typedReason = CloudScribe.Application.Logging.LocalRequestDiagnostics.Describe(error, clock.GetUtcNow()).Reason;
        return typedReason switch
        {
            CloudScribe.Application.Logging.LocalRequestReason.ProviderRejected => "article.provider-rejected-no-retry",
            CloudScribe.Application.Logging.LocalRequestReason.ProviderOutcomeUncertain => "article.provider-outcome-uncertain-no-retry",
            _ => error switch
            {
                OperationCanceledException => "article.cancelled-reconcile",
                PrivateDiagnosticNotReadyException => "article.private-capture-required",
                InvalidDataException => "article.invalid-evidence-reconcile",
                UnauthorizedAccessException => "article.storage-denied-reconcile",
                IOException or Microsoft.Data.Sqlite.SqliteException => "article.storage-failed-reconcile",
                InvalidOperationException => "article.guard-rejected-reconcile",
                _ => "article.unexpected-failure-reconcile",
            }
        };
    }

    private async Task<ChirpWholeArticleJob> GenerateWholeArticleRemainingAsync(ChirpWholeArticleJob job,
        IChirpWholeArticleRuntime runtime, ChirpArticleProgress coverage, IProgress<ChirpArticleProgress>? progress, CancellationToken token)
    {
        while (!coverage.Complete)
        {
            token.ThrowIfCancellationRequested();
            RequireWholeJobLifetime(job);
            var policy = await PolicyAsync(token).ConfigureAwait(false);
            RequireWholeArticleGrant(policy, job.PlanSha256);
            if (!string.Equals(job.PolicySha256, ChirpPolicyValidation.Hash(policy), StringComparison.Ordinal)
                || !string.Equals(job.PlanSha256, coverage.PlanSha256, StringComparison.Ordinal) || ChirpPolicyValidation.IsJobExpired(job, clock.GetUtcNow())
                || job.CompletionBound != ChirpPolicyValidation.IsCompletionBound(policy))
                throw new InvalidOperationException("Article admission expired or exact plan changed. No automatic renewal.");
            var next = coverage.Parts.First(x => !IsAccepted(x));
            if (!next.CurrentlyAdmitted || job.MaximumRecoveryRequests < 2 && !string.Equals(next.Status, "Not requested", StringComparison.Ordinal))
                throw new InvalidOperationException("An existing request requires reconciliation. No automatic retry.");
            if (job.NarrationRecoveryOnly && string.Equals(next.Status, "Not requested", StringComparison.Ordinal))
                return await TransitionWholeArticleAsync(job, ChirpWholeArticleState.Stopped, null, "article.narration-copy-recovery-only", token).ConfigureAwait(false);
            if (string.Equals(next.Status, "Not requested", StringComparison.Ordinal))
                await RequireCurrentNarrationCopyAsync(job.DocumentId, token).ConfigureAwait(false);
            job = await RefreshWholeArticleCatalogIfNeededAsync(job, policy, runtime, token).ConfigureAwait(false);
            job = await TransitionWholeArticleAsync(job, ChirpWholeArticleState.Executing, next.CoverageOrdinal, "article.request-starting", token).ConfigureAwait(false);
            if (!string.Equals(next.Status, "Not requested", StringComparison.Ordinal))
                await RecoverWholeArticleRangeAsync(job, await RecoveryRootForJobAsync(job, next.CoverageOrdinal, token).ConfigureAwait(false), runtime, token).ConfigureAwait(false);
            else
            {
                try { await runtime.GenerateAsync(job, next.CoverageOrdinal, token).ConfigureAwait(false); }
                catch (Exception error) when (job.MaximumRecoveryRequests >= 2
                    && error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException and not OperationCanceledException)
                {
                    var root = await TryRecoveryRootForJobAsync(job, next.CoverageOrdinal, token).ConfigureAwait(false);
                    if (root is not { } qualified) throw;
                    await RecoverWholeArticleRangeAsync(job, qualified, runtime, token).ConfigureAwait(false);
                }
            }
            coverage = await ProgressAsync(job.DocumentId, CancellationToken.None).ConfigureAwait(false);
            if (!IsAccepted(coverage.Parts.Single(x => x.CoverageOrdinal == next.CoverageOrdinal)))
                throw new InvalidOperationException("The request has no verified saved receipt. Stop and reconcile, retaining its hold.");
            job = await TransitionWholeArticleAsync(job, ChirpWholeArticleState.Ready, null, "article.request-saved", CancellationToken.None).ConfigureAwait(false);
            progress?.Report(coverage);
            token.ThrowIfCancellationRequested();
        }
        return job;
    }

    private static bool IsAccepted(ChirpPartProgress part) => part.Reused || string.Equals(part.Status, "Saved accepted audio", StringComparison.Ordinal);

    private async Task<ChirpWholeArticleJob> RefreshWholeArticleCatalogIfNeededAsync(ChirpWholeArticleJob job,
        ChirpArticleAdmissionPolicy policy, IChirpWholeArticleRuntime runtime, CancellationToken token)
    {
        if (!runtime.RequiresCatalogRefresh) return job;
        if (!job.CompletionBound && job.CatalogRefreshes >= policy.WholeArticleGeneration!.MaximumCatalogRefreshes)
            throw new InvalidOperationException("The independently admitted catalog-refresh limit is exhausted.");
        job = await TransitionWholeArticleAsync(job, ChirpWholeArticleState.RefreshingCatalog, null, "article.catalog-refresh-starting", token, countRefresh: true).ConfigureAwait(false);
        await runtime.RefreshCatalogAsync(token).ConfigureAwait(false);
        if (runtime.RequiresCatalogRefresh) throw new InvalidOperationException("Catalog verification made no forward freshness progress; no speech request may start.");
        await RequirePolicyCurrentAsync(policy, token).ConfigureAwait(false);
        return await TransitionWholeArticleAsync(job, ChirpWholeArticleState.Ready, null, "article.catalog-refreshed", token).ConfigureAwait(false);
    }

    private static async Task VerifyWholeArticleOutputAsync(ChirpWholeArticleOutput? output, CancellationToken token)
    {
        if (output is null || output.Bytes < 12 || !ChirpPolicyValidation.IsHash(output.Sha256)
            || !Path.IsPathFullyQualified(output.Path)) throw new InvalidDataException("No verified complete-article output receipt exists.");
        using var stream = File.OpenRead(output.Path);
        if (stream.Length != output.Bytes || !string.Equals(Convert.ToHexStringLower(
            await System.Security.Cryptography.SHA256.HashDataAsync(stream, token).ConfigureAwait(false)), output.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("The completed article output changed; recover locally, never regenerate accepted speech.");
    }
}
