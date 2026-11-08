using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private async Task<ChirpArticleProgress> ReconcileExecutingRecoveryAsync(ChirpWholeArticleJob job,
        IChirpWholeArticleRuntime runtime, ChirpArticleProgress coverage, CancellationToken token)
    {
        if (IsAccepted(coverage.Parts.Single(x => x.CoverageOrdinal == job.ActiveOrdinal))) return coverage;
        var root = await RecoveryRootForJobAsync(job, job.ActiveOrdinal!.Value, token).ConfigureAwait(false);
        await RecoverWholeArticleRangeAsync(job, root, runtime, token).ConfigureAwait(false);
        return await ProgressAsync(job.DocumentId, token).ConfigureAwait(false);
    }

    public async Task<bool> CanRecoverSentenceRejectionAsync(Guid jobId, Guid parentAuthorizationId, CancellationToken token = default)
    {
        try { return (await PrepareRecoveryChildrenAsync(jobId, parentAuthorizationId, token).ConfigureAwait(false)).Count == 2; }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { return false; }
    }

    private async Task RecoverWholeArticleRangeAsync(ChirpWholeArticleJob job, Guid parent,
        IChirpWholeArticleRuntime runtime, CancellationToken token)
    {
        var children = await PrepareRecoveryChildrenAsync(job.JobId, parent, token).ConfigureAwait(false);
        foreach (var child in children)
        {
            token.ThrowIfCancellationRequested();
            int status = await RecoveryChildStatusAsync(child, token).ConfigureAwait(false);
            if (status == 1) continue;
            if (status == 2)
            {
                await RecoverWholeArticleRangeAsync(job, child.AuthorizationId, runtime, token).ConfigureAwait(false); continue;
            }
            try { await runtime.GenerateRecoveryAsync(job, child, token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException and not OperationCanceledException)
            {
                if (!await IsKnownChildRejectionAsync(child, token).ConfigureAwait(false)) throw;
            }
            status = await RecoveryChildStatusAsync(child, CancellationToken.None).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (status == 2) await RecoverWholeArticleRangeAsync(job, child.AuthorizationId, runtime, token).ConfigureAwait(false);
            else if (status != 1) throw new InvalidOperationException("A recovery child lacks a verified accepted receipt; no automatic replay is permitted.");
        }
    }

    private async Task<int> RecoveryChildStatusAsync(ChirpArticleRecoveryChild child, CancellationToken token)
    {
        var job = await ReadWholeArticleJobAsync(child.JobId, token).ConfigureAwait(false);
        var policy = await PolicyAsync(token, policySha256: job.PolicySha256).ConfigureAwait(false);
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var mapping = await ReadRecoveryChildAsync(connection, transaction, child.AuthorizationId, token).ConfigureAwait(false);
        if (mapping is null) return 0;
        if (mapping != child) throw new InvalidOperationException("The existing child belongs to another exact recovery proposal.");
        var rows = await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_owned_attempts", token).ConfigureAwait(false);
        var attempt = rows.Single(x => Equals(x[0], child.AuthorizationId.ToString("N")));
        if (Equals(attempt[16], 0L)) return 0;
        var snapshot = await RecoveryFinancialSnapshot.ReadAsync(connection, transaction, token).ConfigureAwait(false);
        var queue = snapshot.QueueStates.Single(x => Equals(x.Account, attempt[5]) && Equals(x.Key, attempt[7])
            && Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId));
        if (queue.Unresolved != 0) throw new InvalidOperationException("Recovery child outcome is uncertain; automatic recovery stopped.");
        if (string.Equals(queue.DiagnosticCode, "google-accepted", StringComparison.Ordinal))
        {
            if (await ReadRecoveryReceiptAsync(connection, transaction, child, token).ConfigureAwait(false) is null)
                throw new InvalidOperationException("Recover accepted local output before continuing; never dispatch it again.");
            return 1;
        }
        await RequireSentenceRejectionAsync(connection, transaction, policy, attempt, token).ConfigureAwait(false);
        return 2;
    }

    private async Task<Guid> RecoveryRootForJobAsync(ChirpWholeArticleJob job, int ordinal, CancellationToken token)
    {
        var policy = await PolicyAsync(token, policySha256: job.PolicySha256).ConfigureAwait(false);
        RequireRecoveryJob(policy, job);
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        return await RecoverableOrdinalAsync(connection, transaction, policy, job.PlanSha256, ordinal, token).ConfigureAwait(false);
    }

    private async Task<Guid?> TryRecoveryRootForJobAsync(ChirpWholeArticleJob job, int ordinal, CancellationToken token)
    {
        try { return await RecoveryRootForJobAsync(job, ordinal, token).ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { return null; }
    }

    private async Task<bool> IsKnownChildRejectionAsync(ChirpArticleRecoveryChild child, CancellationToken token)
    {
        try { return await RecoveryChildStatusAsync(child, token).ConfigureAwait(false) == 2; }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { return false; }
    }
}
