using System.Reflection;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NarrationCopyWholeArticleMixedRecoveryResumesSameCheckpointButNeverFreshSibling(bool stopped, bool restoreSource)
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var (n, job, first) = await MixedNarrationRecoveryCheckpointAsync(f, stopped); var e = n.Evidence;
        string rootHash = JsonHash(Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts")));
        string receiptHash = JsonHash(Assert.Single(await FinancialRowsAsync(f, "chirp_article_split_receipts")));
        await EditNarrationSourceAsync(f, n.Source);
        e = e with { Service = new(f, e.Admission, f.Paths, e.Clock) };
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        Assert.Equal(e.Article.Proposal.Parts.Count - 1, review.BlockedOrdinaryRequests);
        Assert.Equal(1, review.RemainingRequests);
        Assert.Equal(review.MaximumRecoveryMinorUnits, review.MaximumAdditionalMinorUnits);
        var narrowed = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        Assert.True(narrowed.NarrationRecoveryOnly);
        Assert.Equal(job.JobId, narrowed.JobId); Assert.Equal(job.ExpiresAtUtc, narrowed.ExpiresAtUtc);
        Assert.Equal(job.MaximumAdditionalMinorUnits, narrowed.MaximumAdditionalMinorUnits);
        Assert.Equal(job.MaximumRecoveryMinorUnits, narrowed.MaximumRecoveryMinorUnits);
        if (restoreSource) await RestoreNarrationSourceCheckpointAsync(f, n);
        e = e with { Service = new(f, e.Admission, f.Paths, e.Clock) };
        Assert.True((await e.Service.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken)).NarrationRecoveryOnly);
        var runtime = NarrationRecoveryObserver(f, n, e);
        var result = await e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken);
        await AssertNarrationRecoveryOnlyResultAsync(f, e, runtime, result, first, rootHash, receiptHash);
    }

    private static RecoveryFakeRuntime NarrationRecoveryObserver(Fixture f, NarrationArticleFixture n, ChirpEvidence e) => new(f, e)
    {
        ObserveRecoveryProvider = (child, submitted) =>
        {
            using var wire = JsonDocument.Parse(submitted.CompiledPayload);
            string text = System.Xml.Linq.XElement.Parse(wire.RootElement.GetProperty("input").GetProperty("ssml").GetString()!,
                System.Xml.Linq.LoadOptions.PreserveWhitespace).Value;
            Assert.Equal(ChirpNarrationPlanner.Slice(n.Copy.Narration.Text, child.Range), text);
            Assert.DoesNotContain("https://", text, StringComparison.Ordinal);
        }
    };

    private static async Task AssertNarrationRecoveryOnlyResultAsync(Fixture f, ChirpEvidence e, RecoveryFakeRuntime runtime,
        ChirpWholeArticleJob result, ChirpArticleRecoveryChild first, string rootHash, string receiptHash)
    {
        Assert.Equal(ChirpWholeArticleState.Stopped, result.State);
        Assert.Equal("article.narration-copy-recovery-only", result.ReasonCode);
        Assert.True(result.NarrationRecoveryOnly); Assert.False(runtime.Exported);
        Assert.Equal(0, runtime.RootFailures); Assert.Equal(1, runtime.Calls);
        Assert.NotEqual(first.AuthorizationId, Assert.Single(runtime.Children).AuthorizationId);
        Assert.Equal(rootHash, JsonHash(Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts").ConfigureAwait(false))));
        Assert.Equal(receiptHash, JsonHash((await FinancialRowsAsync(f, "chirp_article_split_receipts").ConfigureAwait(false))
            .Single(x => Equals(x[0], first.AuthorizationId.ToString("N")))));
        var progress = await e.Service.ProgressAsync(e.Document.Id, TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(1, progress.SavedCoverageParts); Assert.Equal("Not requested", progress.Parts[1].Status);
        Assert.Single(await FinancialRowsAsync(f, "chirp_whole_article_jobs").ConfigureAwait(false));
        Assert.Equal(2, (await FinancialRowsAsync(f, "chirp_article_split_attempts").ConfigureAwait(false)).Count);
        Assert.Equal(result, await new ChirpArticleService(f, e.Admission, f.Paths, e.Clock)
            .RunWholeArticleAsync(result.JobId, runtime, token: TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Equal(1, runtime.Calls);
    }

    [Fact]
    public async Task NarrationCopyWholeArticleRecoveryOnlyStartRejectsChangedExistingCheckpoint()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var (n, job, _) = await MixedNarrationRecoveryCheckpointAsync(f, false);
        var e = n.Evidence; await EditNarrationSourceAsync(f, n.Source);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        _ = await ChangeNarrationRecoveryCheckpointAsync(e.Service, job, job.State, "article.synthetic-new-checkpoint");
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken));
        Assert.False((await e.Service.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken)).NarrationRecoveryOnly);
        Assert.Single(await FinancialRowsAsync(f, "chirp_whole_article_jobs"));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_split_attempts"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NarrationCopyWholeArticleRecoveryOnlyCannotBypassUnknownOutcomeOrCorruptChild(bool corruptChild)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var (n, _, _) = await MixedNarrationRecoveryCheckpointAsync(f, false);
        var e = n.Evidence; await EditNarrationSourceAsync(f, n.Source);
        using (var db = f.CreateDbContext())
        {
            if (corruptChild)
            {
                var revision = await db.DocumentRevisions.SingleAsync(x => x.Id == n.Copy.Narration.CurrentRevisionId, TestContext.Current.CancellationToken);
                await File.WriteAllTextAsync(Path.Combine(f.Root, revision.ContentRelativePath!), "corrupt", TestContext.Current.CancellationToken);
            }
            else await db.Database.ExecuteSqlRawAsync("UPDATE google_generation_queue_states SET UnresolvedSubmission=1 WHERE IdempotencyKey IN (SELECT ChildKey FROM chirp_article_attempts);", TestContext.Current.CancellationToken);
        }
        await Assert.ThrowsAnyAsync<Exception>(() => e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_split_attempts"));
    }

    [Fact]
    public void NarrationCopyWholeArticleLegacyJobSerializationOmitsRecoveryOnlyFlag()
    {
        var job = new ChirpWholeArticleJob(Guid.NewGuid(), Guid.NewGuid(), "plan", "policy", "review", 5,
            DateTimeOffset.UnixEpoch.AddHours(1), DateTimeOffset.UnixEpoch, ChirpWholeArticleState.Ready, null, "article.ready");
        var legacy = new
        {
            job.JobId,
            job.DocumentId,
            job.PlanSha256,
            job.PolicySha256,
            job.ReviewSha256,
            job.MaximumAdditionalMinorUnits,
            job.ExpiresAtUtc,
            job.LastObservedAtUtc,
            job.State,
            job.ActiveOrdinal,
            job.ReasonCode,
            job.Output,
            job.CatalogRefreshes
        };
        Assert.Equal(JsonSerializer.Serialize(legacy), JsonSerializer.Serialize(job));
        Assert.Equal(ChirpPolicyValidation.Hash(legacy), ChirpPolicyValidation.Hash(job));
    }

    private static async Task<(NarrationArticleFixture Narration, ChirpWholeArticleJob Job, ChirpArticleRecoveryChild First)> MixedNarrationRecoveryCheckpointAsync(Fixture f, bool stopped)
    {
        var n = await NarrationArticleAsync(f, whole: true, recovery: true, mixedRecovery: true).ConfigureAwait(false);
        Assert.True(n.Evidence.Article.Proposal.Parts.Count > 1);
        var (job, first) = await CreateAcceptedChildCheckpointAsync(f, n.Evidence).ConfigureAwait(false);
        if (stopped) job = await ChangeNarrationRecoveryCheckpointAsync(n.Evidence.Service, job, ChirpWholeArticleState.Stopped,
            "article.private-capture-required").ConfigureAwait(false);
        return (n, job, first);
    }

    private static async Task<ChirpWholeArticleJob> ChangeNarrationRecoveryCheckpointAsync(ChirpArticleService service,
        ChirpWholeArticleJob job, ChirpWholeArticleState state, string reason)
    {
        var transition = typeof(ChirpArticleService).GetMethod("TransitionWholeArticleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return await ((Task<ChirpWholeArticleJob>)transition.Invoke(service,
            [job, state, 1, reason, TestContext.Current.CancellationToken, null, false])!).ConfigureAwait(false);
    }

    private static async Task RestoreNarrationSourceCheckpointAsync(Fixture f, NarrationArticleFixture n)
    {
        using var db = f.CreateDbContext();
        var source = await db.Documents.SingleAsync(x => x.Id == n.Source.Id, TestContext.Current.CancellationToken).ConfigureAwait(false);
        source.CurrentRevisionId = n.Source.CurrentRevisionId; source.DraftText = n.Source.Text;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.NotNull(await new EfNarrationCopyStore(f, new(f.Paths), n.Evidence.Clock)
            .FindAsync(n.Copy.Narration.Id, TestContext.Current.CancellationToken).ConfigureAwait(false));
    }
}
