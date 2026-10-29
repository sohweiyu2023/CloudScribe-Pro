using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task ChirpClaimHasOneDurableOwnerAndNoGenericSpendFallback()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f); var intent = ChirpIntent(e);
        var review = await e.Service.PrepareAsync(intent, TestContext.Current.CancellationToken);
        var approved = await e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        var envelope = intent.Envelope with { ChirpAuthorizationId = approved.AuthorizationId };
        var claim = ChirpClaim(intent, approved); var store = new GoogleGenerationPersistedQueueStateStore(f);
        Assert.NotNull(await e.Service.LoadSpendAsync(envelope, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryClaimSubmissionAsync(claim, TestContext.Current.CancellationToken));
        Assert.True(await e.Service.TryClaimAuthorizedAsync(envelope, intent.ProjectId, "USD", 2, 1, claim, TestContext.Current.CancellationToken));
        Assert.False(await e.Service.TryClaimAuthorizedAsync(envelope, intent.ProjectId, "USD", 2, 1,
            claim with { LocalAttemptId = Guid.NewGuid().ToString("N") }, TestContext.Current.CancellationToken));
        Assert.Null(await e.Service.LoadSpendAsync(envelope, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.PrepareAsync(ChirpIntent(e, 2), TestContext.Current.CancellationToken));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        var genericSpend = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => genericSpend.SaveApprovedAsync(GoogleGenerationSpendAuthorization.Create(intent.Envelope, "USD", 2, 1, 1), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ChirpFinalAdmissionRevocationRollsBackEvenConsumedClaim(int finalRead)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f); var intent = ChirpIntent(e);
        var review = await e.Service.PrepareAsync(intent, TestContext.Current.CancellationToken);
        var approved = await e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        e.Admission.RevokeAt = e.Admission.Reads + finalRead;
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.TryClaimAuthorizedAsync(intent.Envelope with { ChirpAuthorizationId = approved.AuthorizationId },
            intent.ProjectId, "USD", 2, 1, ChirpClaim(intent, approved), TestContext.Current.CancellationToken));
        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT Consumed AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT SubmissionAttempted AS Value FROM google_generation_queue_states WHERE IdempotencyKey LIKE 'chirp-article:%'").SingleAsync(TestContext.Current.CancellationToken));
    }
}
