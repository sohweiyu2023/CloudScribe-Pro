using CloudScribe.App.Composition;
using CloudScribe.Application.Diagnostics;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleRecoveryProductionRoutingTests
{
    [Fact]
    public async Task UnhealthyRequiredTrendHistoryBlocksActualRuntimeBeforeReservationOrProviderResolution()
    {
        using var f = new Fixture(); await f.InitializeChirpAsync(); f.AdmitSyntheticWholeArticle(0);
        var articles = f.CreateArticles(); var persisted = new GoogleGenerationPersistedQueueStateStore(f);
        var spend = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, articles: articles);
        var owner = new GoogleGenerationProductionSubmissionStateOwner(spend);
        var preparation = new ChirpArticlePreparationService(articles, f.ChirpCompiler, owner, TimeProvider.System);
        var source = new GoogleGenerationProductionRuntimeRequestSource(spend, owner.ResolveCurrentAsync);
        var exporter = new ArticleAudioExportService(f, new EfDocumentLibrary(f, new DocumentContentStore(f.WholePaths), TimeProvider.System),
            persisted, f.WholePaths, new WholeNative(), chirpArticles: articles);
        int resolved = 0;
        var runtime = new ChirpWholeArticleRuntime(preparation, articles, source,
            (_, _) => { resolved++; throw new InvalidOperationException("Synthetic execution must not be resolved."); },
            exporter, f.Selection, Path.Combine(f.WholeRoot, "unused.mp3"), Path.Combine(f.WholeRoot, "unused.exe"), trendHistory: new FailedTrendHistory());
        var review = await articles.PrepareWholeArticleAsync(f.Selection.DocumentId, TestContext.Current.CancellationToken);
        var job = await articles.StartWholeArticleAsync(new(review.PreviewId, review.ReviewSha256, review.MaximumAdditionalMinorUnits, true, true),
            TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => articles.RunWholeArticleAsync(job.JobId, runtime,
            token: TestContext.Current.CancellationToken));
        Assert.Equal(LocalRequestReason.TrendHistoryUnavailable, LocalRequestDiagnostics.Describe(error, DateTimeOffset.UtcNow).Reason);
        Assert.Equal(0, resolved);
        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_split_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }

    private sealed class FailedTrendHistory : IGoogleTrendHistoryHealth
    {
        public bool IsAvailable => false;
        public string FailureReason => "WriteFailed";
        public int? LastFailureHResult => -2147024784;
        public bool HasPartialTail => false;
        public long RecordsWritten => 0;
        public void RequireReady() => throw new InvalidOperationException("Synthetic required history is unavailable.");
    }
}
