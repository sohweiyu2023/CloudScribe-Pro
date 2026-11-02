using System.Text.Json;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task WholeArticleRefreshIsCountedDurablyBeforeBootstrapAndStopsAtAdmittedLimit()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await WholeEvidenceAsync(f);
        e.Admission.Policy = e.Admission.Policy! with { WholeArticleGeneration = e.Admission.Policy!.WholeArticleGeneration! with { MaximumCatalogRefreshes = 1 } };
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var runtime = new WholeFakeRuntime(f, e) { RequireRefreshBeforeEveryRequest = true }; runtime.ExpireCatalog();
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Equal(1, runtime.Refreshes); Assert.Single(runtime.Ordinals);
        var stopped = await e.Service.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken);
        Assert.Equal(1, stopped.CatalogRefreshes); Assert.Equal(ChirpWholeArticleState.Stopped, stopped.State);
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_receipts"));
    }

    [Fact]
    public async Task WholeArticleExpiredAuthorityDoesNotRefreshReserveOrDispatch()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await WholeEvidenceAsync(f);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        e.Clock.Now += TimeSpan.FromHours(2);
        var runtime = new WholeFakeRuntime(f, e); runtime.ExpireCatalog();
        await Assert.ThrowsAnyAsync<Exception>(() => e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Equal(0, runtime.Refreshes); Assert.Empty(runtime.Ordinals);
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Fact]
    public async Task WholeArticleWorkerLockPreventsConcurrentProviderUse()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await WholeEvidenceAsync(f);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        using var worker = new FileStream(Path.Combine(f.Root, ".whole-article-worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var runtime = new WholeFakeRuntime(f, e);
        await Assert.ThrowsAsync<IOException>(() => e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Empty(runtime.Ordinals); Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Fact]
    public async Task WholeArticleConsentAndAuditRollbackTogetherAndCannotBeRewritten()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await WholeEvidenceAsync(f);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        using (var db = f.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER synthetic_whole_io BEFORE INSERT ON chirp_whole_article_events BEGIN SELECT RAISE(ABORT,'synthetic-disk-full'); END;", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SqliteException>(() => e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_whole_article_jobs"));
        using (var db = f.CreateDbContext()) await db.Database.ExecuteSqlRawAsync("DROP TRIGGER synthetic_whole_io;", TestContext.Current.CancellationToken);
        review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        using var final = f.CreateDbContext();
        await Assert.ThrowsAsync<SqliteException>(() => final.Database.ExecuteSqlRawAsync("UPDATE chirp_whole_article_events SET JobSha256='tampered';", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SqliteException>(() => final.Database.ExecuteSqlRawAsync("DELETE FROM chirp_whole_article_events;", TestContext.Current.CancellationToken));
        await final.Database.ExecuteSqlInterpolatedAsync($"UPDATE chirp_whole_article_jobs SET JobJson={JsonSerializer.Serialize(job with { MaximumAdditionalMinorUnits = 9999 })};", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => e.Service.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken));
    }
}
