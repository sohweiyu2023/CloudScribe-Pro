using CloudScribe.App.Composition;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CatalogReopenPreservesCompletedOrUncertainDurableRequestAndNeverReclaimsIt(bool expired, bool uncertain)
    {
        await using var f = await CatalogReopenFixture.CreateAsync();
        await f.InitializeNativeCompilerAsync();
        var document = await f.Disk.NewLibrary().CreateAsync("Synthetic held request", "One exact synthetic request.", TestContext.Current.CancellationToken);
        GoogleGenerationProductionPendingApprovalStateOwner.PendingState pending;
        using (var first = f.NewModel())
        {
            await f.OpenAsync(first);
            var selection = await f.SelectionAsync(first, document, "en-US-Neural2-D");
            pending = Assert.Single((await f.CompileArticleAsync(selection, document)).Pending);
        }
        var spend = new GoogleGenerationSpendAuthorizationStore(f.Factory, f.Clock);
        var queue = new GoogleGenerationPersistedQueueStateStore(f.Factory);
        var authorization = GoogleGenerationSpendAuthorization.Create(pending.Envelope, pending.Currency, pending.Scale,
            pending.CurrentEstimateMinorUnits, pending.CurrentEstimateMinorUnits);
        await spend.SaveApprovedAsync(authorization, TestContext.Current.CancellationToken);
        var initial = pending.Snapshot.CurrentState;
        await queue.SaveAsync(initial, TestContext.Current.CancellationToken);
        var claim = initial with
        {
            SubmissionAttempted = true,
            UnresolvedSubmission = true,
            LocalAttemptId = Guid.NewGuid().ToString("N"),
            DiagnosticCode = "google-dispatch-claimed",
            PayloadSha256 = pending.Envelope.CompiledPayloadSha256,
            PayloadBytes = pending.Envelope.CompiledPayloadBytes,
            OutcomeObservedAtUnixMilliseconds = f.Clock.GetUtcNow().ToUnixTimeMilliseconds()
        };
        Assert.True(await queue.TryClaimSubmissionAsync(claim, TestContext.Current.CancellationToken));
        var outcome = claim with { UnresolvedSubmission = uncertain, DiagnosticCode = uncertain ? "google-transport-unknown" : "google-accepted" };
        await queue.SaveOutcomeAsync(outcome, TestContext.Current.CancellationToken);
        if (expired) await f.ApplyReopenMutationAsync("expired");
        using var reopened = f.NewModel();
        await f.OpenAsync(reopened);
        var newSelection = await f.SelectionAsync(reopened, document, "en-US-Neural2-D");
        var after = Assert.Single((await f.CompileArticleAsync(newSelection, document)).Pending);
        Assert.Equal(authorization, await spend.LoadApprovedAsync(pending.Envelope, TestContext.Current.CancellationToken));
        Assert.Equal(outcome, await queue.LoadAsync(initial.AccountId, initial.OperationStableId, initial.IdempotencyKey, TestContext.Current.CancellationToken));
        Assert.False(await queue.TryClaimSubmissionAsync(claim, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.SaveAsync(initial, TestContext.Current.CancellationToken));
        if (expired)
        {
            Assert.NotEqual(pending.Envelope.RequestRevision, after.Envelope.RequestRevision);
            Assert.Null(await spend.LoadApprovedAsync(after.Envelope, TestContext.Current.CancellationToken));
        }
        else
        {
            Assert.Equal(pending.Envelope, after.Envelope);
            Assert.Equal(initial.IdempotencyKey, after.Snapshot.CurrentState.IdempotencyKey);
        }
        Assert.Null(await f.PendingOwner.ResolveCurrentAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, f.Handler.PaidRequests);
    }
}
