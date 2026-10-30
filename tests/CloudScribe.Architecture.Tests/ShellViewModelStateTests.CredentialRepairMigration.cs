using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task LegacyPreparedCredentialRepairResumesAfterSuccessorMigrationWithoutFinancialDigestReordering()
    {
        await using var f = await RepairFixture.CreateAsync(migration: GoogleCredentialRepairs.MigrationId);
        await f.Disk.NewLibrary().CreateAsync("Synthetic preserved draft", "Exact local draft.", TestContext.Current.CancellationToken);
        await f.SqlAsync("""
            INSERT INTO chirp_article_plans VALUES('synthetic-plan','synthetic-document','synthetic-revision','synthetic-google','synthetic-project','{}','{}','[]');
            INSERT INTO chirp_article_attempts VALUES('ffffffffffffffffffffffffffffffff','synthetic-plan',1,'old-policy','old-approval','synthetic-google','synthetic-project',
                'synthetic-failed1','old-review1','{}','USD',2,1,1,'old-financial',1,1,'fffffffffffffffffffffffffffffffe');
            INSERT INTO chirp_article_attempts VALUES('11111111111111111111111111111111','synthetic-plan',2,'old-policy','old-approval','synthetic-google','synthetic-project',
                'synthetic-failed2','old-review2','{}','USD',2,1,1,'old-financial',1,1,'11111111111111111111111111111112');
            """);
        string retained = await f.RetainedAsync();
        string audio = Path.Combine(f.Disk.Paths.RootDirectory, "retained-synthetic.mp3");
        await File.WriteAllBytesAsync(audio, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        Guid repair = Guid.NewGuid(); string reference = "google-repair." + repair.ToString("N");
        var account = await f.CurrentAsync();
        using var candidate = await f.Verifier.VerifyAsync(f.Json.AsMemory(), account, RepairFixture.Project, RepairFixture.Email, reference, TestContext.Current.CancellationToken);
        string financial = await LegacyRepairDigestAsync(f);
        using (var db = f.Factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO google_credential_repairs VALUES({repair.ToString("N")},{RepairFixture.Account},{JsonSerializer.Serialize(account)},{financial},{JsonSerializer.Serialize(candidate.Identity)},{reference},{"Prepared"},{new string('a', 64)},{f.Clock.Now.AddMinutes(5).ToUnixTimeMilliseconds()},{f.Clock.Now.ToUnixTimeMilliseconds()})", TestContext.Current.CancellationToken);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }
        var service = f.NewService();
        var review = await service.PrepareAsync(RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken);
        Assert.True(review.Resuming); Assert.Equal(repair, review.RepairId); Assert.Equal(reference, review.NewCredentialReference);
        await service.ConfirmAsync(review.ReviewId, review.ReviewSha256, TestContext.Current.CancellationToken);
        Assert.Equal(3, f.Vault.Writes);
        Assert.Equal(retained, await f.RetainedAsync());
        Assert.Equal(financial, await LegacyRepairDigestAsync(f));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(audio, TestContext.Current.CancellationToken));
        Assert.Equal(1L, await f.SqlAsync("SELECT COUNT(*) FROM google_credential_repair_events WHERE Phase='Committed'"));
        Assert.Equal(0L, await f.SqlAsync("SELECT COUNT(*) FROM chirp_article_successor_attempts"));
    }

    private static async Task<string> LegacyRepairDigestAsync(RepairFixture f)
    {
        // Frozen pre-successor shape and rowid ordering, independently reconstructed from the old implementation.
        var value = new
        {
            Queue = await LegacyRepairRowsAsync(f, "google_generation_queue_states").ConfigureAwait(false),
            Spend = await LegacyRepairRowsAsync(f, "google_generation_spend_authorizations").ConfigureAwait(false),
            Recovery = await LegacyRepairRowsAsync(f, "google_recovery_attempts").ConfigureAwait(false),
            Scopes = await LegacyRepairRowsAsync(f, "google_recovery_scopes").ConfigureAwait(false),
            Transitions = await LegacyRepairRowsAsync(f, "google_recovery_grant_transitions").ConfigureAwait(false),
            Chirp = await LegacyRepairRowsAsync(f, "chirp_article_attempts").ConfigureAwait(false),
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    }

    private static async Task<object?[][]> LegacyRepairRowsAsync(RepairFixture f, string table)
    {
        using var db = f.Factory.CreateDbContext(); var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid";
        using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        List<object?[]> rows = [];
        while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))
        { object[] row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row.Select(x => x is DBNull ? null : x).ToArray()); }
        return rows.ToArray();
    }
}
