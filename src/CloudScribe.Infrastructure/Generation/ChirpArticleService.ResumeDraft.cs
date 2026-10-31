using CloudScribe.Application.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    public static async Task<ChirpArticleAdmissionPolicy> ReadHistoricalResumePolicyAsync(string path, CancellationToken token = default) =>
        ChirpPolicyFiles.Parse<ChirpArticleAdmissionPolicy>(await ChirpPolicyFiles.ReadAsync(path, 8 * 1024 * 1024, token).ConfigureAwait(false));

    public async Task<ChirpReservedResumeSource> InspectReservedResumeSourceAsync(Guid document, int ordinal,
        ChirpArticleAdmissionPolicy historicalPolicy, CancellationToken token = default)
    {
        var history = ChirpPolicyFiles.Parse<ChirpArticleAdmissionPolicy>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(historicalPolicy));
        if (!string.Equals(history.ProfileRootSha256, InstalledRecoveryAdmissionStore.ProfileHash(paths.RootDirectory), StringComparison.Ordinal)) throw ResumeRejected();
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var rows = await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_owned_attempts", token).ConfigureAwait(false);
        var article = history.Articles.SingleOrDefault(x => x.Proposal.DocumentId == document) ?? throw ResumeRejected();
        var matches = rows.Where(x => Equals(x[1], article.Proposal.PlanSha256) && Equals(x[2], (long)ordinal) && Equals(x[16], 0L) && x[17] is null).ToArray();
        if (matches.Length != 1 || !Equals(matches[0][3], ChirpPolicyValidation.Hash(history))) throw ResumeRejected();
        var row = matches[0];
        ChirpPolicyValidation.RequireValid(history, DateTimeOffset.FromUnixTimeMilliseconds((long)row[15]!));
        var financial = await ReadFinancialAsync(connection, transaction, history, token).ConfigureAwait(false);
        if (!Equals(row[14], financial.Sha256)) throw ResumeRejected();
        await RequireReadyAsync(connection, transaction, financial, Guid.ParseExact((string)row[0]!, "N"), token).ConfigureAwait(false);
        await RequireNoReservedOutcomeAsync(connection, transaction, row, token).ConfigureAwait(false);
        var current = await _evidence.ResolveAsync(connection, transaction, history, document, financial.Old, token, requirePersistedLegacy: true).ConfigureAwait(false);
        var part = current.Article.Proposal.Parts.Single(x => x.CoverageOrdinal == ordinal && x.Range.ReusedReceiptSha256 is null);
        return new(Guid.ParseExact((string)row[0]!, "N"), new(current.Document, current.Article.Proposal, part, ChirpNarrationPlanner.Slice(current.Document.Text, part.Range)));
    }

    public async Task<DateTimeOffset> InspectResumeCatalogExpiryAsync(string account, Guid catalog, CancellationToken token = default)
    {
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ExpiresAtUnixMilliseconds FROM provider_capability_snapshots WHERE ProviderStableId=$provider AND AccountId=$account AND lower(Id)=lower($id);";
        command.Parameters.AddWithValue("$provider", GoogleGenerationProvider.StableProviderId); command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$id", catalog.ToString("D"));
        var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        return value is long expiry ? DateTimeOffset.FromUnixTimeMilliseconds(expiry) : throw ResumeRejected();
    }

    public async Task<string?> InspectResumeMappingHeadAsync(Guid authorization, CancellationToken token = default)
    {
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var chain = await ReadResumeChainAsync(connection, transaction, authorization, token).ConfigureAwait(false);
        return chain.Count == 0 ? null : chain[^1].Sha256;
    }
}
