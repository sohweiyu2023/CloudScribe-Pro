using System.Text.Json;
using CloudScribe.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task SuccessorMigrationAddsOnlyEmptyAuditWhileRetainingExhaustedGrantAndAllFinancialRows()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        await StageSuccessorAsync(f, e);
        string[] tables = ["google_generation_queue_states", "google_generation_spend_authorizations", "google_recovery_attempts", "google_recovery_scopes"];
        var before = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string table in tables) before[table] = JsonSerializer.Serialize(await FinancialRowsAsync(f, table));
        using (var db = f.CreateDbContext())
        {
            // Reconstruct the immediately preceding synthetic schema, with its real consumed fixture rows retained.
            await db.Database.ExecuteSqlRawAsync("DROP TABLE google_recovery_grant_transitions", TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM __EFMigrationsHistory WHERE MigrationId={GoogleRecoverySuccessorGrants.MigrationId}", TestContext.Current.CancellationToken);
        }
        await f.InitializeAsync();
        foreach (string table in tables) Assert.Equal(before[table], JsonSerializer.Serialize(await FinancialRowsAsync(f, table)));
        using var current = f.CreateDbContext();
        Assert.Equal(0, await current.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_grant_transitions").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await current.Database.SqlQueryRaw<long>("SELECT SUM(Consumed) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }
}
