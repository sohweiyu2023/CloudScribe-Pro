using System.Text.Json;
using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task FreshSuccessorPreservesOldHoldsAndResumesItsOwnSingleReservation()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var s = await StageSuccessorAsync(f, e);
        var old = await FinancialRowsAsync(f, "google_recovery_attempts");
        var service = s.Service(f);
        Assert.Equal(s.Prior.IdempotencyKey, await service.FindLatestKeyAsync(s.Policy.Predecessors.Single(), TestContext.Current.CancellationToken));
        var review = await service.PrepareAsync(s.Intent(e), 3, TestContext.Current.CancellationToken);
        Assert.Equal(253, review.Budget.RetainedCommittedMinorUnits); Assert.Equal(70, review.Budget.RemainingAllocationMinorUnits);
        var approved = await service.ApproveAsync(Confirmation(review), TestContext.Current.CancellationToken);
        var restarted = s.Service(f);
        var pending = await restarted.LoadPendingForCurrentEnvelopeAsync(e.Intent.Envelope, e.Intent.ProjectId,
            e.Intent.DocumentId, e.Intent.RevisionId, TestContext.Current.CancellationToken);
        Assert.Equal(approved, pending!.Approval);
        var envelope = e.Intent.Envelope with { RecoveryAuthorizationId = approved.AuthorizationId };
        Assert.NotNull(await restarted.LoadExecutionAuthorizationAsync(envelope, TestContext.Current.CancellationToken));
        Assert.True(await restarted.TryClaimAuthorizedAsync(envelope, e.Intent.ProjectId, "USD", 2, 3, ClaimRecovery(e, approved), TestContext.Current.CancellationToken));
        Assert.False(await restarted.TryClaimAuthorizedAsync(envelope, e.Intent.ProjectId, "USD", 2, 3, ClaimRecovery(e, approved), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.PrepareAsync(s.Intent(e), 3, TestContext.Current.CancellationToken));
        var rows = await FinancialRowsAsync(f, "google_recovery_attempts");
        Assert.Equal(JsonSerializer.Serialize(old[0]), JsonSerializer.Serialize(rows[0]));
        Assert.Equal(2, rows.Count);
        Assert.Equal(s.Failed, await new GoogleGenerationPersistedQueueStateStore(f).LoadAsync(s.Failed.AccountId,
            s.Failed.OperationStableId, s.Failed.IdempotencyKey, TestContext.Current.CancellationToken));
        Assert.Equal(e.OldSpend, await new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System).LoadApprovedAsync(e.Intent.Envelope, TestContext.Current.CancellationToken));
        await AssertConcreteBudgetAsync(f, e, s.Budget, 256, 67);
        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_grant_transitions").SingleAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("payload")]
    [InlineData("prior-child")]
    [InlineData("prior-state")]
    [InlineData("old-grant")]
    [InlineData("old-root")]
    [InlineData("expired")]
    [InlineData("scope")]
    [InlineData("allocation")]
    [InlineData("approval")]
    [InlineData("financial")]
    public async Task FreshSuccessorRejectsMismatchedAuthorityWithoutFinancialWrites(string mismatch)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var s = await StageSuccessorAsync(f, e); var p = s.Policy.Predecessors.Single(); var link = p.Successor!;
        p = mismatch switch
        {
            "account" => p with { AccountId = "wrong-account" },
            "payload" => p with { OriginalPayloadSha256 = new string('f', 64) },
            "prior-child" => p with { Successor = link with { PriorChildKey = "wrong-child" } },
            "prior-state" => p with { Successor = link with { PriorStateSha256 = new string('f', 64) } },
            "old-grant" => p with { Successor = link with { PriorAdmissionSha256 = new string('f', 64) } },
            "old-root" => p with { HistoricalStateSha256 = new string('f', 64) },
            "expired" => p with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
            "scope" => p with { Successor = link with { AllocationTransition = link.AllocationTransition! with { PriorScopeSha256 = new string('f', 64) } } },
            "allocation" => p with { Successor = link with { AllocationTransition = null } },
            "approval" => p with { Successor = link with { SourceApprovalSha256 = new string('f', 64) } },
            "financial" => p with { Successor = link with { FinancialStateSha256 = new string('f', 64) } },
            _ => throw new InvalidOperationException()
        };
        WriteSuccessorPolicy(f, s.Policy with { Predecessors = [RehashSuccessor(p)] });
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service(f).PrepareAsync(s.Intent(e), 3, TestContext.Current.CancellationToken));
        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_grant_transitions").SingleAsync(TestContext.Current.CancellationToken));
    }
}
