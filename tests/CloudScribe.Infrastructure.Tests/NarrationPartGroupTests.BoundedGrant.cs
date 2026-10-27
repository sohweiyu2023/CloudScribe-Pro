using CloudScribe.Infrastructure.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData(1, 3, 4)]
    [InlineData(1, 2, 3)]
    [InlineData(0, 3, 3)]
    [InlineData(1, -1, 3)]
    public async Task GrantRejectsOutOfBoundsReservationBeforeCreatingFinancialRows(int attempts, long cap, long requested)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        e.Association.Value = e.Association.Value! with { MaximumNewAttempts = attempts, MaximumAdditionalReservationMinorUnits = cap };
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, e.Budget, e.Association);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(e.Intent, requested, TestContext.Current.CancellationToken));
        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_generation_queue_states").SingleAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 5)]
    public async Task ConsumedUncertainAttemptDoesNotReleaseAdmissionCountOrCumulativeReservation(int attempts, long cap)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        e.Association.Value = e.Association.Value! with { MaximumNewAttempts = attempts, MaximumAdditionalReservationMinorUnits = cap };
        var (admission, budget) = await CreateInstalledBudgetAsync(f, e);
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, budget, admission);
        var preview = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        var approved = await service.ApproveAsync(Confirmation(preview), TestContext.Current.CancellationToken);
        var envelope = e.Intent.Envelope with { RecoveryAuthorizationId = approved.AuthorizationId };
        var restarted = new GoogleExplicitRecoveryService(f, TimeProvider.System, budget, admission);
        Assert.Equal(approved, (await restarted.LoadPendingForCurrentEnvelopeAsync(e.Intent.Envelope, e.Intent.ProjectId,
            e.Intent.DocumentId, e.Intent.RevisionId, TestContext.Current.CancellationToken))!.Approval);
        Assert.True(await restarted.TryClaimAuthorizedAsync(envelope, e.Intent.ProjectId, "USD", 2, 3, ClaimRecovery(e, approved), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.PrepareAsync(e.Intent with { PreviousIdempotencyKey = approved.IdempotencyKey }, 3, TestContext.Current.CancellationToken));
        await AssertConcreteBudgetAsync(f, e, budget, 253, 67);
        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, await db.Database.SqlQueryRaw<long>("SELECT SUM(ReservedMinorUnits) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameGrantCannotExpandLimitsAtApprovalOrAfterRestart(bool alreadyApproved)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        e.Association.Value = e.Association.Value! with { MaximumNewAttempts = 1, MaximumAdditionalReservationMinorUnits = 3 };
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, e.Budget, e.Association);
        var preview = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        var approved = alreadyApproved ? await service.ApproveAsync(Confirmation(preview), TestContext.Current.CancellationToken) : null;
        e.Association.Value = e.Association.Value with { MaximumNewAttempts = 2, MaximumAdditionalReservationMinorUnits = 6 };
        if (approved is null)
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(Confirmation(preview), TestContext.Current.CancellationToken));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => new GoogleExplicitRecoveryService(f, TimeProvider.System, e.Budget, e.Association)
                .LoadExecutionAuthorizationAsync(e.Intent.Envelope with { RecoveryAuthorizationId = approved.AuthorizationId }, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(2, 6)]
    [InlineData(1, 2)]
    public async Task ChangedGrantAtDirectClaimCannotConsumeOrReleaseReservation(int attempts, long cap)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        e.Association.Value = e.Association.Value! with { MaximumNewAttempts = 1, MaximumAdditionalReservationMinorUnits = 3 };
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, e.Budget, e.Association);
        var preview = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        var approved = await service.ApproveAsync(Confirmation(preview), TestContext.Current.CancellationToken);
        var envelope = e.Intent.Envelope with { RecoveryAuthorizationId = approved.AuthorizationId };
        e.Association.Value = e.Association.Value with { MaximumNewAttempts = attempts, MaximumAdditionalReservationMinorUnits = cap };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TryClaimAsync(approved, ClaimRecovery(e, approved),
            envelope, e.Intent.ProjectId, "USD", 2, 3, TestContext.Current.CancellationToken));
        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT Consumed AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, await db.Database.SqlQueryRaw<long>("SELECT ReservedMinorUnits AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(e.Previous, await new GoogleGenerationPersistedQueueStateStore(f).LoadAsync(e.Previous.AccountId,
            e.Previous.OperationStableId, e.Previous.IdempotencyKey, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentOneShotApprovalsReserveExactlyOnceAndInterruptedApprovalRetainsAllowance()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        e.Association.Value = e.Association.Value! with { MaximumNewAttempts = 1, MaximumAdditionalReservationMinorUnits = 3 };
        var a = new GoogleExplicitRecoveryService(f, TimeProvider.System, e.Budget, e.Association);
        var preview = await a.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        using (var db = f.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER synthetic_grant_interruption BEFORE INSERT ON google_recovery_attempts BEGIN SELECT RAISE(ABORT,'synthetic'); END;", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SqliteException>(() => a.ApproveAsync(Confirmation(preview), TestContext.Current.CancellationToken));
        using (var db = f.CreateDbContext())
        {
            Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_scopes").SingleAsync(TestContext.Current.CancellationToken));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER synthetic_grant_interruption;", TestContext.Current.CancellationToken);
        }
        var b = new GoogleExplicitRecoveryService(f, TimeProvider.System, e.Budget, e.Association);
        var pa = await a.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        var pb = await b.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        async Task<bool> Approve(GoogleExplicitRecoveryService service, GoogleRecoveryPreview review)
        {
            try { await service.ApproveAsync(Confirmation(review), TestContext.Current.CancellationToken).ConfigureAwait(false); return true; }
            catch (InvalidOperationException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Approve(a, pa), Approve(b, pb)), result => result);
        using var final = f.CreateDbContext();
        Assert.Equal(1, await final.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(253, await final.Database.SqlQueryRaw<long>("SELECT RetainedFloorMinorUnits AS Value FROM google_recovery_scopes").SingleAsync(TestContext.Current.CancellationToken));
    }
}
