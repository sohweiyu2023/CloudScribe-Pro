using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    public async Task<ChirpArticlePlanningInspection> InspectStoredArticleAsync(Guid documentId, string voice, string language,
        decimal planningUsdPerMillionCharacters, CancellationToken token = default)
    {
        if (planningUsdPerMillionCharacters is <= 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(planningUsdPerMillionCharacters));
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        string plan = await UniqueStoredPlanAsync(connection, transaction, documentId, token).ConfigureAwait(false);
        var stored = await StoredAsync(connection, transaction, plan, token).ConfigureAwait(false);
        var proposal = stored.Article.Proposal;
        if (!string.Equals(voice, proposal.VoiceName, StringComparison.Ordinal) || !string.Equals(language, proposal.LanguageCode, StringComparison.Ordinal))
            throw new InvalidOperationException("The selected voice or language differs from the saved audio plan. No automatic voice switch is permitted.");
        var financial = await RecoveryFinancialSnapshot.ReadAsync(connection, transaction, token).ConfigureAwait(false);
        await _evidence.ResolveSnapshotAsync(connection, transaction, stored.Article, stored.AccountId, stored.ProjectId, financial, token).ConfigureAwait(false);
        var receipts = await SegmentReceiptsAsync(connection, transaction, stored, financial, token).ConfigureAwait(false);
        foreach (var receipt in receipts) await VerifyMediaAsync(receipt.Audio.AudioPath, receipt.Audio, token).ConfigureAwait(false);
        var attempts = await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_owned_attempts", token).ConfigureAwait(false);
        var coverage = DescribeProgress(stored.Article, receipts, attempts, financial.QueueStates);
        var remaining = coverage.Parts.Where(x => !IsAccepted(x)).ToArray();
        return new(documentId, plan, proposal.VoiceName, proposal.LanguageCode, proposal.SourceScalars,
            coverage.SavedCoverageParts, coverage.TotalCoverageParts, remaining.Length, remaining.Sum(x => x.Scalars),
            planningUsdPerMillionCharacters, remaining.Sum(x => x.Scalars) * planningUsdPerMillionCharacters / 1_000_000m,
            remaining.Sum(x => checked((long)decimal.Ceiling(x.Scalars * planningUsdPerMillionCharacters / 10_000m))),
            remaining.Where(x => !string.Equals(x.Status, "Not requested", StringComparison.Ordinal)).Select(x => x.CoverageOrdinal).ToArray());
    }
}
