using System.Text.Json;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task LegacyMigrationPreservesEveryOldFieldAndHoldButCannotResumeUnboundedAuthority()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, e.Budget, e.Association);
        var preview = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        var approved = await service.ApproveAsync(Confirmation(preview), TestContext.Current.CancellationToken);
        // Recreate the prior release's physical 27-column shape with synthetic retained history.
        // Never exercise a downgrade against a real profile or discard a financial row.
        using (var db = f.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE google_recovery_attempts DROP COLUMN GrantMaximumNewAttempts;", TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE google_recovery_attempts DROP COLUMN GrantMaximumAdditionalMinorUnits;", TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlRawAsync("DROP INDEX IX_google_recovery_admission;", TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM __EFMigrationsHistory WHERE MigrationId={GoogleRecoveryAdmissionLimits.MigrationId};", TestContext.Current.CancellationToken);
        }
        string old = await LegacyFieldsAsync(f);
        var queueStore = new GoogleGenerationPersistedQueueStateStore(f);
        var child = await queueStore.LoadAsync(e.Previous.AccountId, e.Previous.OperationStableId, approved.IdempotencyKey, TestContext.Current.CancellationToken);
        await f.InitializeAsync();
        Assert.Equal(old, await LegacyFieldsAsync(f));
        using (var db = f.CreateDbContext())
        {
            Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_attempts WHERE GrantMaximumNewAttempts IS NULL AND GrantMaximumAdditionalMinorUnits IS NULL AND ReservedMinorUnits=3 AND Consumed=0").SingleAsync(TestContext.Current.CancellationToken));
            Assert.Equal(253, await db.Database.SqlQueryRaw<long>("SELECT RetainedFloorMinorUnits AS Value FROM google_recovery_scopes").SingleAsync(TestContext.Current.CancellationToken));
        }
        var envelope = e.Intent.Envelope with { RecoveryAuthorizationId = approved.AuthorizationId };
        Assert.Null(await service.LoadExecutionAuthorizationAsync(envelope, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoadPendingForCurrentEnvelopeAsync(e.Intent.Envelope, e.Intent.ProjectId,
            e.Intent.DocumentId, e.Intent.RevisionId, TestContext.Current.CancellationToken));
        Assert.False(await service.TryClaimAuthorizedAsync(envelope, e.Intent.ProjectId, "USD", 2, 3, ClaimRecovery(e, approved), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(e.Intent with { PreviousIdempotencyKey = approved.IdempotencyKey }, 3, TestContext.Current.CancellationToken));
        Assert.Equal(child, await queueStore.LoadAsync(e.Previous.AccountId, e.Previous.OperationStableId, approved.IdempotencyKey, TestContext.Current.CancellationToken));
        Assert.Equal(old, await LegacyFieldsAsync(f));
    }

    private static async Task<string> LegacyFieldsAsync(Fixture fixture)
    {
        using var db = fixture.CreateDbContext();
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT * FROM google_recovery_attempts ORDER BY AuthorizationId;";
        using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        List<object?[]> rows = [];
        while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))
            rows.Add(Enumerable.Range(0, 27).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
        return JsonSerializer.Serialize(rows);
    }
}
