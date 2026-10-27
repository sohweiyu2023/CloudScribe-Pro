using CloudScribe.App.Composition;
using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleRecoveryProductionRoutingTests
{
    [Theory]
    [InlineData("budget")]
    [InlineData("authority")]
    [InlineData("financial")]
    public async Task ResumeConfirmationBindsFreshBudgetFinancialAndPolicyWithoutNewReservation(string change)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var recovery = f.CreateRecovery();
        var store = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, recovery);
        var first = new GoogleRecoveryPreparationService(f.Admission, f.Compiler, recovery,
            new GoogleGenerationProductionSubmissionStateOwner(store), TimeProvider.System);
        var originalReview = await first.PrepareAsync(f.Selection, f.RootKey, 3, TestContext.Current.CancellationToken);
        await first.ApproveAsync(f.Selection, Confirmation(originalReview), TestContext.Current.CancellationToken);
        var original = await recovery.FindApprovedAsync(originalReview.ReviewSha256, TestContext.Current.CancellationToken);
        var owner = new GoogleGenerationProductionSubmissionStateOwner(store);
        var resumedService = new GoogleRecoveryPreparationService(f.Admission, f.Compiler, f.CreateRecovery(), owner, TimeProvider.System);
        var resumed = await resumedService.PrepareAsync(f.Selection, original!.IdempotencyKey, 3, TestContext.Current.CancellationToken);
        Assert.False(string.Equals(original.ReviewSha256, resumed.ReviewSha256, StringComparison.Ordinal));
        if (string.Equals(change, "budget", StringComparison.Ordinal))
            f.ChangeSyntheticReviewedPolicy(p => p with { Budget = p.Budget with { ProjectCommittedMinorUnits = 251, AllocationCommittedMinorUnits = 11 } });
        if (string.Equals(change, "authority", StringComparison.Ordinal))
            f.ChangeSyntheticReviewedPolicy(p => p with { ExpiresAtUtc = p.ExpiresAtUtc.AddHours(1) });
        if (string.Equals(change, "financial", StringComparison.Ordinal))
            await new GoogleGenerationPersistedQueueStateStore(f).SaveAsync(new(f.Selection.AccountStableId,
                GoogleGenerationProvider.SynthesizeOperationStableId, "unaccounted-financial-change", false, null), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resumedService.ApproveAsync(f.Selection, Confirmation(resumed), TestContext.Current.CancellationToken));
        Assert.Null(await owner.ResolveCurrentAsync(TestContext.Current.CancellationToken));
        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, await db.Database.SqlQueryRaw<int>("SELECT ReservedMinorUnits AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT Consumed AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        if (!string.Equals(change, "financial", StringComparison.Ordinal))
        {
            var fresh = await resumedService.PrepareAsync(f.Selection, original.IdempotencyKey, 3, TestContext.Current.CancellationToken);
            Assert.False(string.Equals(resumed.ReviewSha256, fresh.ReviewSha256, StringComparison.Ordinal));
            await resumedService.ApproveAsync(f.Selection, Confirmation(fresh), TestContext.Current.CancellationToken);
            Assert.Equal(original.AuthorizationId, (await owner.ResolveCurrentAsync(TestContext.Current.CancellationToken))!.SubmissionEnvelope.RecoveryAuthorizationId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartResumesSameDurableReservationWithoutNewChildOrGenericSave(bool lostActivationAcknowledgement)
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var firstRecovery = f.CreateRecovery();
        var firstStore = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, firstRecovery);
        var firstOwner = new GoogleGenerationProductionSubmissionStateOwner(lostActivationAcknowledgement ? new FailActivationOnce(firstStore) : firstStore);
        var first = new GoogleRecoveryPreparationService(f.Admission, f.Compiler, firstRecovery, firstOwner, TimeProvider.System);
        var review = await first.PrepareAsync(f.Selection, f.RootKey, 3, TestContext.Current.CancellationToken);
        Assert.Null(review.ExistingApproval);
        if (lostActivationAcknowledgement)
            await Assert.ThrowsAsync<IOException>(() => first.ApproveAsync(f.Selection, Confirmation(review), TestContext.Current.CancellationToken));
        else
            await first.ApproveAsync(f.Selection, Confirmation(review), TestContext.Current.CancellationToken);
        var original = await firstRecovery.FindApprovedAsync(review.ReviewSha256, TestContext.Current.CancellationToken);
        Assert.NotNull(original);
        first.Discard(review.PreviewId); // No durable state is removed when old UI state disappears.

        var restartedRecovery = f.CreateRecovery();
        var restartedStore = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, restartedRecovery);
        var restartedOwner = new GoogleGenerationProductionSubmissionStateOwner(restartedStore);
        var restarted = new GoogleRecoveryPreparationService(f.Admission, f.Compiler, restartedRecovery, restartedOwner, TimeProvider.System);
        string key = Assert.Single(await restarted.ListCurrentHistoricalKeysAsync(f.Selection, TestContext.Current.CancellationToken));
        Assert.Equal(original.IdempotencyKey, key);
        var resume = await restarted.PrepareAsync(f.Selection, key, 3, TestContext.Current.CancellationToken);
        Assert.Equal(original, resume.ExistingApproval);
        Assert.Equal(253, resume.Budget.RetainedCommittedMinorUnits); Assert.Equal(67, resume.Budget.RemainingAllocationMinorUnits);
        Assert.Null(await restartedOwner.ResolveCurrentAsync(TestContext.Current.CancellationToken));
        await restarted.ApproveAsync(f.Selection, Confirmation(resume), TestContext.Current.CancellationToken);
        var current = await restartedOwner.ResolveCurrentAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(current); Assert.Equal(original.AuthorizationId, current.SubmissionEnvelope.RecoveryAuthorizationId);
        Assert.Equal(original.IdempotencyKey, current.Snapshot.ProviderRequest.IdempotencyKey);
        var request = await new GoogleGenerationProductionRuntimeRequestSource(restartedStore, restartedOwner.ResolveCurrentAsync)
            .ResolveAsync(TestContext.Current.CancellationToken);
        Assert.Equal(original.AuthorizationId, request.SubmissionEnvelope.RecoveryAuthorizationId);
        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, await db.Database.SqlQueryRaw<int>("SELECT ReservedMinorUnits AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT Consumed AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM google_generation_spend_authorizations").SingleAsync(TestContext.Current.CancellationToken));
        Assert.True((await new GoogleGenerationPersistedQueueStateStore(f).LoadAsync(f.Selection.AccountStableId,
            GoogleGenerationProvider.SynthesizeOperationStableId, f.RootKey, TestContext.Current.CancellationToken))!.UnresolvedSubmission);
    }

    [Theory]
    [InlineData("different-document")]
    [InlineData("different-capability")]
    [InlineData("different-maximum")]
    public async Task PendingRecoveryCannotBeReplacedByDifferentCurrentEvidence(string change)
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var recovery = f.CreateRecovery(); var owner = new GoogleGenerationProductionSubmissionStateOwner(new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, recovery));
        var service = new GoogleRecoveryPreparationService(f.Admission, f.Compiler, recovery, owner, TimeProvider.System);
        var first = await service.PrepareAsync(f.Selection, f.RootKey, 3, TestContext.Current.CancellationToken);
        await service.ApproveAsync(f.Selection, Confirmation(first), TestContext.Current.CancellationToken);
        var original = await recovery.FindApprovedAsync(first.ReviewSha256, TestContext.Current.CancellationToken);
        var restarted = new GoogleRecoveryPreparationService(f.Admission, f.Compiler, f.CreateRecovery(), owner, TimeProvider.System);
        var selection = string.Equals(change, "different-document", StringComparison.Ordinal) ? f.Selection with { DocumentId = Guid.NewGuid() } : f.Selection;
        if (string.Equals(change, "different-capability", StringComparison.Ordinal)) f.Compiler.Capability = "changed-capability";
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.PrepareAsync(selection, original!.IdempotencyKey,
            string.Equals(change, "different-maximum", StringComparison.Ordinal) ? 4 : 3, TestContext.Current.CancellationToken));
        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, await db.Database.SqlQueryRaw<int>("SELECT ReservedMinorUnits AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }

    private static GoogleRecoveryApprovalConfirmation Confirmation(GoogleRecoveryPreview review) => new(review.PreviewId, review.ReviewSha256, review.AdditionalMaximumMinorUnits, true, true);

    private sealed class FailActivationOnce(IGoogleGenerationSpendAuthorizationStore inner) : IGoogleGenerationSpendAuthorizationStore
    {
        private bool _failed;
        public Task SaveApprovedAsync(GoogleGenerationSpendAuthorization authorization, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Recovery must never save generic approval.");
        public Task<GoogleGenerationSpendAuthorization?> LoadApprovedAsync(GoogleGenerationSubmissionEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (!_failed) { _failed = true; throw new IOException("Synthetic activation acknowledgement loss after durable approval."); }
            return inner.LoadApprovedAsync(envelope, cancellationToken);
        }
    }
}
