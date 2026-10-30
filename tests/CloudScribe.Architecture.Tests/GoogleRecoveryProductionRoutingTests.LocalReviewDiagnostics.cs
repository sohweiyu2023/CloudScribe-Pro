using CloudScribe.App.Composition;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleRecoveryProductionRoutingTests
{
    [Fact]
    public async Task ActualArticlePreparationReportsExpiredLocalReviewWithoutActivationOrWrites()
    {
        using var f = new Fixture(); await f.InitializeChirpAsync(); var articles = f.CreateArticles();
        var owner = new GoogleGenerationProductionSubmissionStateOwner(new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, articles: articles));
        var clock = new LocalReviewClock();
        var preparation = new ChirpArticlePreparationService(articles, f.ChirpCompiler, owner, clock);
        var review = await preparation.PrepareAsync(f.Selection, 1, TestContext.Current.CancellationToken);
        clock.Now = clock.Now.AddMinutes(10).AddTicks(1);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.ApproveAsync(f.Selection, ChirpConfirm(review), TestContext.Current.CancellationToken));
        var details = LocalRequestDiagnostics.Describe(error, clock.Now);
        Assert.Equal(LocalRequestReason.ReviewExpired, details.Reason);
        Assert.Equal(review.ExpiresAtUtc, details.AdmissionExpiresUtc);
        Assert.Null(await owner.ResolveCurrentAsync(TestContext.Current.CancellationToken));
        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_successor_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }

    private sealed class LocalReviewClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
