using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task ChirpAcceptedAudioCompletesAfterCallerCancellationWithoutAnotherRequest()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var accepted = await AcceptChirpAsync(f, e.Service, ChirpIntent(e), 1);
        using var caller = new CancellationTokenSource(); await caller.CancelAsync();
        string file = await e.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, caller.Token);
        Assert.Equal(accepted.Outcome.Response!.MediaBytes.ToArray(), await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_receipts"));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Fact]
    public async Task ChirpLocalWriteFailureRetainsOwnedAudioAndRepairsWithoutPolicyOrProvider()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var accepted = await AcceptChirpAsync(f, e.Service, ChirpIntent(e), 1);
        string root = Path.Combine(f.Root, "accepted-chirp-article-audio");
        await File.WriteAllTextAsync(root, "synthetic-storage-obstruction", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => e.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_receipts"));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_output_journals"));
        byte[] expected = accepted.Outcome.Response!.MediaBytes.ToArray();
        // The service owns a snapshot, even if the caller reuses its original buffers.
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(accepted.Outcome.Response.MediaBytes, out var backing))
            backing.Array![backing.Offset] = 0;
        File.Delete(root); e.Admission.Policy = null; e.Clock.Now = e.Clock.Now.AddHours(2);
        Assert.Equal(1, await e.Service.RecoverLocalOutputsAsync(TestContext.Current.CancellationToken));
        string file = Assert.Single(Directory.GetFiles(root, "*.mp3"));
        Assert.Equal(expected, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_receipts"));
    }

    [Fact]
    public async Task ChirpCancelledReceiptPublicationCanBeRecoveredLocallyAfterRestart()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var accepted = await AcceptChirpAsync(f, e.Service, ChirpIntent(e), 1);
        using (var db = f.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER synthetic_chirp_receipt_cancel BEFORE INSERT ON chirp_article_receipts BEGIN SELECT RAISE(ABORT,'synthetic-interruption'); END;", TestContext.Current.CancellationToken);
        using var caller = new CancellationTokenSource(); await caller.CancelAsync();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => e.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, caller.Token));
        using (var db = f.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER synthetic_chirp_receipt_cancel;", TestContext.Current.CancellationToken);
        e.Admission.Policy = null;
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        Assert.Equal(1, await restarted.RecoverLocalOutputsAsync(TestContext.Current.CancellationToken));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_receipts"));
    }
}
