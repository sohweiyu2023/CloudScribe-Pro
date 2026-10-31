using CloudScribe.Application.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    public async Task<ChirpResolvedSegment> ResolveSegmentAsync(Guid documentId, int ordinal, string accountId,
        string projectId, string voice, string language, CancellationToken cancellationToken = default)
    {
        var policy = await PolicyAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(policy.AccountId, accountId, StringComparison.Ordinal) || !string.Equals(policy.ProjectId, projectId, StringComparison.Ordinal))
            throw new InvalidOperationException("The current account/project does not match independently admitted article scope.");
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var financial = await ReadFinancialAsync(connection, transaction, policy, cancellationToken).ConfigureAwait(false);
        var current = await _evidence.ResolveAsync(connection, transaction, policy, documentId, financial.Old, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(voice, current.Article.Proposal.VoiceName, StringComparison.Ordinal)
            || !string.Equals(language, current.Article.Proposal.LanguageCode, StringComparison.Ordinal)
            || current.Article.AllowedSegments.Count(x => x.CoverageOrdinal == ordinal) != 1)
            throw new InvalidOperationException("The selected voice/language or internal request is outside current reviewed admission.");
        var part = current.Article.Proposal.Parts.Single(x => x.CoverageOrdinal == ordinal && x.Range.ReusedReceiptSha256 is null);
        await RequirePolicyCurrentAsync(policy, cancellationToken).ConfigureAwait(false);
        return new(current.Document, current.Article.Proposal, part, ChirpNarrationPlanner.Slice(current.Document.Text, part.Range));
    }

    public async Task<ChirpArticleProgress> ProgressAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var admittedNow = await admission.LoadVerifiedAsync(cancellationToken).ConfigureAwait(false);
        var refining = admittedNow?.Articles.SingleOrDefault(x => x.Proposal.DocumentId == documentId && x.Proposal.Refinement is not null);
        if (refining is not null)
        {
            var financial = await ReadFinancialAsync(connection, transaction, admittedNow!, cancellationToken).ConfigureAwait(false);
            var current = await _evidence.ResolveAsync(connection, transaction, admittedNow!, documentId, financial.Old, cancellationToken).ConfigureAwait(false);
            using var count = ChirpArticleSql.Command(connection, transaction, "SELECT COUNT(*) FROM chirp_article_plans WHERE PlanSha256=$plan;", ("$plan", refining.Proposal.PlanSha256));
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 0)
                return DescribeProgress(current.Article, [], [], []);
        }
        if (!await HasStoredPlanAsync(documentId, cancellationToken).ConfigureAwait(false))
        {
            var policy = await PolicyAsync(cancellationToken).ConfigureAwait(false);
            var financial = await ReadFinancialAsync(connection, transaction, policy, cancellationToken).ConfigureAwait(false);
            var current = await _evidence.ResolveAsync(connection, transaction, policy, documentId, financial.Old, cancellationToken).ConfigureAwait(false);
            return DescribeProgress(current.Article, [], [], []);
        }
        string plan = await UniqueStoredPlanAsync(connection, transaction, documentId, cancellationToken).ConfigureAwait(false);
        var stored = await StoredAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);
        var snapshot = await RecoveryFinancialSnapshot.ReadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await _evidence.ResolveSnapshotAsync(connection, transaction, stored.Article, stored.AccountId, stored.ProjectId, snapshot, cancellationToken).ConfigureAwait(false);
        var receipts = await SegmentReceiptsAsync(connection, transaction, stored, snapshot, cancellationToken).ConfigureAwait(false);
        foreach (var receipt in receipts) await VerifyMediaAsync(receipt.Audio.AudioPath, receipt.Audio, cancellationToken).ConfigureAwait(false);
        var rows = await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_owned_attempts", cancellationToken).ConfigureAwait(false);
        var policyNow = await admission.LoadVerifiedAsync(cancellationToken).ConfigureAwait(false);
        var active = policyNow?.Articles.SingleOrDefault(x => string.Equals(x.Proposal.PlanSha256, plan, StringComparison.Ordinal));
        IReadOnlyList<ChirpSegmentAllowance> allowed = active?.AllowedSegments ?? [];
        if (await ResumePolicyAsync(null, cancellationToken).ConfigureAwait(false) is { } resume)
        {
            var target = rows.SingleOrDefault(x => Equals(x[0], resume.AuthorizationId.ToString("N")) && Equals(x[1], plan) && Equals(x[16], 0L));
            if (target is not null) allowed = [new((int)(long)target[2]!, (long)target[13]!)];
        }
        return DescribeProgress(stored.Article with { AllowedSegments = allowed }, receipts, rows, snapshot.QueueStates);
    }

    private static ChirpArticleProgress DescribeProgress(AdmittedChirpArticle article, IReadOnlyList<ChirpSegmentAcceptedAudio> receipts,
        IReadOnlyList<object?[]> attempts, IReadOnlyList<RecoveryFinancialSnapshot.QueueState> queues)
    {
        List<ChirpPartProgress> progress = [];
        foreach (var part in article.Proposal.Parts)
        {
            var rows = attempts.Where(x => Equals(x[1], article.Proposal.PlanSha256) && Equals(x[2], (long)part.CoverageOrdinal)).ToArray();
            var latest = rows.FirstOrDefault(x => Equals(x[16], 0L))
                ?? rows.FirstOrDefault(x => queues.Any(q => Equals(q.Key, x[7]) && string.Equals(q.DiagnosticCode, "google-accepted", StringComparison.Ordinal)))
                ?? rows.FirstOrDefault();
            bool saved = receipts.Any(x => x.CoverageOrdinal == part.CoverageOrdinal);
            string status = part.Range.ReusedReceiptSha256 is not null ? "Reused accepted audio" : saved ? "Saved accepted audio"
                : latest is null ? "Not requested" : Equals(latest[16], 0L) ? "Reserved; review the same identity to resume"
                : queues.Any(x => Equals(x.Key, latest[7]) && string.Equals(x.DiagnosticCode, "google-accepted", StringComparison.Ordinal))
                    ? "Accepted; recover local output" : "Stopped; reconcile result, no automatic retry";
            progress.Add(new(part.CoverageOrdinal, part.Range.EndScalarExclusive - part.Range.StartScalar, status, part.Range.ReusedReceiptSha256 is not null,
                article.AllowedSegments.Any(x => x.CoverageOrdinal == part.CoverageOrdinal)));
        }
        bool complete = false;
        try { _ = ChirpArticleAudioCoverage.RequireComplete(article.Proposal, receipts, article.ReusedAudio); complete = true; }
        catch (InvalidOperationException) { /* Partial coverage is displayed without exposing an export path. */ }
        return new(article.Proposal.DocumentId, article.Proposal.PlanSha256, receipts.Count + article.ReusedAudio.Count,
            article.Proposal.Parts.Count, complete, progress.AsReadOnly());
    }
}
