using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChirpAdmissionOrRetainedReservationBlocksCompetingOrdinaryAndRecoveryClaims(bool retainedAfterExpiry)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var old = await RecoveryEvidenceAsync(f);
        var recovery = new GoogleExplicitRecoveryService(f, TimeProvider.System, old.Budget, old.Association);
        var review = await recovery.PrepareAsync(old.Intent, 3, TestContext.Current.CancellationToken);
        var approved = await recovery.ApproveAsync(Confirmation(review), TestContext.Current.CancellationToken);
        var queues = new GoogleGenerationPersistedQueueStateStore(f);
        var initial = new GoogleGenerationPersistedQueueState(old.Intent.Envelope.AccountId, GoogleGenerationProvider.SynthesizeOperationStableId, "synthetic-competing-fresh", false, null);
        await queues.SaveAsync(initial, TestContext.Current.CancellationToken);
        var e = await ChirpEvidenceAsync(f);
        if (retainedAfterExpiry)
        {
            var articleReview = await e.Service.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken);
            await e.Service.ApproveAsync(ChirpConfirmation(articleReview), TestContext.Current.CancellationToken);
            e.Admission.Policy = null;
        }
        var guardedQueues = new GoogleGenerationPersistedQueueStateStore(f, e.Admission);
        var claim = initial with
        {
            SubmissionAttempted = true,
            UnresolvedSubmission = true,
            LocalAttemptId = Guid.NewGuid().ToString("N"),
            DiagnosticCode = "google-dispatch-claimed",
            PayloadSha256 = old.Intent.Envelope.CompiledPayloadSha256,
            PayloadBytes = old.Intent.Envelope.CompiledPayloadBytes,
            OutcomeObservedAtUnixMilliseconds = 1
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => guardedQueues.TryClaimSubmissionAsync(claim, TestContext.Current.CancellationToken));
        var guardedRecovery = new GoogleExplicitRecoveryService(f, TimeProvider.System, old.Budget, old.Association, e.Admission);
        await Assert.ThrowsAsync<InvalidOperationException>(() => guardedRecovery.TryClaimAsync(approved, ClaimRecovery(old, approved),
            old.Intent.Envelope with { RecoveryAuthorizationId = approved.AuthorizationId }, old.Intent.ProjectId, "USD", 2, 3, TestContext.Current.CancellationToken));
        Assert.Equal(initial, await queues.LoadAsync(initial.AccountId, initial.OperationStableId, initial.IdempotencyKey, TestContext.Current.CancellationToken));
        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT Consumed AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(retainedAfterExpiry ? 1 : 0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }
}
