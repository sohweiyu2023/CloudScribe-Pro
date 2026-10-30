using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task CompletedBadRequestDoesNotRenewConsumedArticleAttemptAfterRestart()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var evidence = await ChirpEvidenceAsync(fixture);
        var intent = ChirpIntent(evidence);
        var review = await evidence.Service.PrepareAsync(intent, TestContext.Current.CancellationToken);
        var approved = await evidence.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        var envelope = intent.Envelope with { ChirpAuthorizationId = approved.AuthorizationId };
        var claim = ChirpClaim(intent, approved);
        Assert.True(await evidence.Service.TryClaimAuthorizedAsync(envelope, intent.ProjectId, "USD", 2, 1,
            claim, TestContext.Current.CancellationToken));
        var store = new GoogleGenerationPersistedQueueStateStore(fixture, evidence.Admission);
        var failed = claim with
        {
            UnresolvedSubmission = false,
            DiagnosticCode = "google-http-400",
            ErrorStatus = GoogleErrorStatus.InvalidArgument,
            OutcomeObservedAtUnixMilliseconds = claim.OutcomeObservedAtUnixMilliseconds + 1,
        };
        await store.SaveOutcomeAsync(failed, TestContext.Current.CancellationToken);

        var restarted = new ChirpArticleService(fixture, evidence.Admission, fixture.Paths, evidence.Clock);
        Assert.Null(await restarted.LoadSpendAsync(envelope, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.PrepareAsync(intent, TestContext.Current.CancellationToken));
        Assert.Equal(failed, await store.LoadAsync(failed.AccountId, failed.OperationStableId, failed.IdempotencyKey,
            TestContext.Current.CancellationToken));
        Assert.Single(await FinancialRowsAsync(fixture, "chirp_article_attempts"));
    }
}
