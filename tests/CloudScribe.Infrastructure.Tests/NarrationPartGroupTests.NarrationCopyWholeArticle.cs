using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("prepare")]
    [InlineData("start")]
    [InlineData("run")]
    public async Task NarrationCopyWholeArticleRechecksOriginalBeforeFreshAuthority(string stage)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var n = await NarrationArticleAsync(f, whole: true); var e = n.Evidence;
        if (string.Equals(stage, "prepare", StringComparison.Ordinal))
        {
            await EditNarrationSourceAsync(f, n.Source);
            await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken));
        }
        else
        {
            var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
            if (string.Equals(stage, "start", StringComparison.Ordinal))
            {
                await EditNarrationSourceAsync(f, n.Source);
                await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken));
            }
            else
            {
                var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
                await EditNarrationSourceAsync(f, n.Source);
                var runtime = new WholeFakeRuntime(f, e);
                await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
                Assert.Empty(runtime.Ordinals);
                Assert.Equal(job.JobId, (await e.Service.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken)).JobId);
                Assert.Single(await FinancialRowsAsync(f, "chirp_whole_article_jobs"));
            }
        }
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
        if (!string.Equals(stage, "run", StringComparison.Ordinal)) Assert.Empty(await FinancialRowsAsync(f, "chirp_whole_article_jobs"));
    }

    [Fact]
    public async Task NarrationCopyWholeArticleCompletedJobRemainsReadableAfterOriginalChanges()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var n = await NarrationArticleAsync(f, whole: true); var e = n.Evidence;
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var runtime = new WholeFakeRuntime(f, e);
        var completed = await e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken);
        await EditNarrationSourceAsync(f, n.Source);
        var rereview = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        Assert.Equal(0, rereview.RemainingRequests);
        Assert.Equal(completed, await e.Service.StartWholeArticleAsync(WholeConfirmation(rereview), TestContext.Current.CancellationToken));
        Assert.Equal(completed, await e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Equal(review.RemainingRequests, runtime.Ordinals.Count);
        Assert.Single(await FinancialRowsAsync(f, "chirp_whole_article_jobs"));
    }

    [Fact]
    public async Task NarrationCopyWholeArticleExactChildRecoverySurvivesOriginalEditWithoutRootReplay()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var n = await NarrationArticleAsync(f, whole: true, recovery: true); var e = n.Evidence;
        Assert.Single(e.Article.Proposal.Parts);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        List<string> submittedTexts = [];
        var inner = new RecoveryFakeRuntime(f, e)
        {
            ObserveRecoveryProvider = (child, submitted) =>
            {
                using var wire = System.Text.Json.JsonDocument.Parse(submitted.CompiledPayload);
                string text = System.Xml.Linq.XElement.Parse(wire.RootElement.GetProperty("input").GetProperty("ssml").GetString()!,
                    System.Xml.Linq.LoadOptions.PreserveWhitespace).Value;
                Assert.Equal(CloudScribe.Application.Generation.ChirpNarrationPlanner.Slice(n.Copy.Narration.Text, child.Range), text);
                Assert.DoesNotContain("https://", text, StringComparison.Ordinal);
                submittedTexts.Add(text);
            }
        };
        var runtime = new NarrationSourceEditingRecoveryRuntime(f, n, inner);
        var completed = await e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Complete, completed.State);
        Assert.Equal(1, inner.RootFailures);
        Assert.Equal(2, inner.Children.Count);
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Equal(2, (await FinancialRowsAsync(f, "chirp_article_split_attempts")).Count);
        Assert.Equal(n.Copy.Narration.Text, string.Concat(submittedTexts));
        Assert.Equal(completed, await e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Equal(1, inner.RootFailures);
        Assert.Equal(2, inner.Children.Count);
    }

    [Fact]
    public async Task NarrationCopyWholeArticleSourceEditAfterAcceptedRangeStopsOnlyRemainingOrdinaryWork()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var n = await NarrationArticleAsync(f, whole: true); var e = n.Evidence;
        Assert.True(e.Article.Proposal.Parts.Count > 1);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var inner = new WholeFakeRuntime(f, e);
        var runtime = new NarrationSourceEditingWholeRuntime(f, n, inner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Single(inner.Ordinals);
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_receipts"));
        var progress = await e.Service.ProgressAsync(e.Document.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, progress.SavedCoverageParts);
        Assert.Equal("Not requested", progress.Parts[1].Status);
        var stopped = await e.Service.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Stopped, stopped.State);
        Assert.Equal(stopped, await e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Single(inner.Ordinals);
    }

    private sealed class NarrationSourceEditingWholeRuntime(Fixture fixture, NarrationArticleFixture narration, WholeFakeRuntime inner)
        : IChirpWholeArticleRuntime
    {
        public async Task GenerateAsync(ChirpWholeArticleJob job, int ordinal, CancellationToken cancellationToken)
        {
            await inner.GenerateAsync(job, ordinal, cancellationToken).ConfigureAwait(false);
            await EditNarrationSourceAsync(fixture, narration.Source).ConfigureAwait(false);
        }
        public Task<ChirpWholeArticleOutput> ExportAsync(ChirpWholeArticleJob job, CancellationToken cancellationToken)
            => inner.ExportAsync(job, cancellationToken);
    }

    private sealed class NarrationSourceEditingRecoveryRuntime(Fixture fixture, NarrationArticleFixture narration, RecoveryFakeRuntime inner)
        : IChirpWholeArticleRuntime
    {
        public async Task GenerateAsync(ChirpWholeArticleJob job, int ordinal, CancellationToken cancellationToken)
        {
            try { await inner.GenerateAsync(job, ordinal, cancellationToken).ConfigureAwait(false); }
            finally { await EditNarrationSourceAsync(fixture, narration.Source).ConfigureAwait(false); }
        }
        public Task GenerateRecoveryAsync(ChirpWholeArticleJob job, ChirpArticleRecoveryChild child, CancellationToken cancellationToken)
            => inner.GenerateRecoveryAsync(job, child, cancellationToken);
        public Task<ChirpWholeArticleOutput> ExportAsync(ChirpWholeArticleJob job, CancellationToken cancellationToken)
            => inner.ExportAsync(job, cancellationToken);
    }
}
