using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class Stage3MigrationTests
{
    [Fact]
    public async Task ChirpAdditiveMigrationPreservesEveryPriorBusinessRowAndRefusesHistoryDiscard()
    {
        string root = CreateTemporaryRoot(), file = Path.Combine(root, "pre-article.db");
        try
        {
            using var db = CreateContext(file);
            await db.GetService<IMigrator>().MigrateAsync(GoogleRecoverySuccessorGrants.MigrationId, TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlRawAsync("INSERT INTO documents (Id,Title,DraftText,CreatedAtUnixMilliseconds,UpdatedAtUnixMilliseconds,Status,IsFavorite,CurrentRevisionId,ConcurrencyVersion) VALUES ({0},{1},{2},1,1,0,0,NULL,1);",
                [Guid.NewGuid(), "Synthetic preserved draft", "Synthetic exact content"], TestContext.Current.CancellationToken);
            var queue = new GoogleGenerationPersistedQueueStateStore(new ChirpMigrationFactory(file));
            var initial = new GoogleGenerationPersistedQueueState("synthetic-account", GoogleGenerationProvider.SynthesizeOperationStableId, "synthetic-prior-held", false, null);
            await queue.SaveAsync(initial, TestContext.Current.CancellationToken);
            Assert.True(await queue.TryClaimSubmissionAsync(initial with
            {
                SubmissionAttempted = true,
                UnresolvedSubmission = true,
                LocalAttemptId = Guid.NewGuid().ToString("N"),
                DiagnosticCode = "google-dispatch-claimed",
                PayloadSha256 = new string('a', 64),
                PayloadBytes = 100,
                OutcomeObservedAtUnixMilliseconds = 1
            }, TestContext.Current.CancellationToken));
            var envelope = new GoogleGenerationSubmissionEnvelope(initial.AccountId, "synthetic-reference", "synthetic-capability", "synthetic-pricing", 1,
                "synthetic-voice", "MP3", new string('a', 64), 100);
            await new GoogleGenerationSpendAuthorizationStore(new ChirpMigrationFactory(file), TimeProvider.System).SaveApprovedAsync(
                GoogleGenerationSpendAuthorization.Create(envelope, "USD", 2, 3, 3), TestContext.Current.CancellationToken);
            var before = await ChirpBusinessRowsAsync(db);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var after = await ChirpBusinessRowsAsync(db);
            foreach (var (table, rows) in before) Assert.Equal(rows, after[table]);
            Assert.Equal(4, after.Count - before.Count);
            Assert.All(after.Where(x => !before.ContainsKey(x.Key)), x => Assert.Equal("[]", x.Value));
            await Assert.ThrowsAsync<NotSupportedException>(() => db.GetService<IMigrator>().MigrateAsync(GoogleRecoverySuccessorGrants.MigrationId, TestContext.Current.CancellationToken));
            Assert.Contains(ChirpArticlePlans.MigrationId, await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken), StringComparer.Ordinal);
            foreach (var (table, rows) in before) Assert.Equal(rows, (await ChirpBusinessRowsAsync(db))[table]);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); DeleteTemporaryRoot(root); }
    }

    private static async Task<Dictionary<string, string>> ChirpBusinessRowsAsync(CloudScribeDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var tablesCommand = connection.CreateCommand();
        tablesCommand.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EF%' ORDER BY name;";
        List<string> tables = [];
        using (var reader = await tablesCommand.ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false)) tables.Add(reader.GetString(0));
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" ORDER BY rowid;";
            using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            List<object?[]> rows = [];
            while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))
            { object[] values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(values.Select(x => x is DBNull ? null : x).ToArray()); }
            result.Add(table, JsonSerializer.Serialize(rows));
        }
        return result;
    }

    private sealed class ChirpMigrationFactory(string file) : IDbContextFactory<CloudScribeDbContext>
    { public CloudScribeDbContext CreateDbContext() => CreateContext(file); }
}
