using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task ExplicitCompletionContinuityCrossesOldCutoffAndResumesUntouchedOrdinalWithoutResetOrReplay()
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var (e, old, stopped) = await UntouchedCompletionCheckpointAsync(f);
        e.Clock.Now = old.ExpiresAtUtc.AddMinutes(2);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        Assert.True(review.CompletionBound); Assert.Equal(old.ExpiresAtUtc, review.ExpiresAtUtc);
        var same = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        Assert.Equal(stopped.JobId, same.JobId); Assert.Equal(stopped.ExpiresAtUtc, same.ExpiresAtUtc);
        Assert.True(same.CompletionBound); Assert.Equal(stopped.MaximumAdditionalMinorUnits, same.MaximumAdditionalMinorUnits);
        var runtime = new WholeFakeRuntime(f, e) { RequireRefreshBeforeEveryRequest = true };
        var done = await e.Service.RunWholeArticleAsync(same.JobId, runtime, token: TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Complete, done.State);
        Assert.Equal(Enumerable.Range(1, e.Article.Proposal.Parts.Count), runtime.Ordinals);
        Assert.Equal(Math.Max(0, e.Article.Proposal.Parts.Count - 1), runtime.Refreshes);
        int count = runtime.Ordinals.Count;
        Assert.Equal(done, await new ChirpArticleService(f, e.Admission, f.Paths, e.Clock)
            .RunWholeArticleAsync(same.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Equal(count, runtime.Ordinals.Count);
        Assert.Equal(old.Budget.ProjectCeilingMinorUnits, e.Admission.Policy!.Budget.ProjectCeilingMinorUnits);
        TestContext.Current.TestOutputHelper!.WriteLine("SYNTHETIC_COMPLETION_BOUND_POLICY_BASE64="
            + Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(e.Admission.Policy)));
    }

    [Theory]
    [InlineData("head")]
    [InlineData("budget")]
    [InlineData("deadline")]
    public async Task CompletionContinuityRejectsChangedCheckpointOrFinancialScope(string changed)
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var (e, old, stopped) = await UntouchedCompletionCheckpointAsync(f);
        e.Clock.Now = old.ExpiresAtUtc.AddMinutes(2);
        var p = e.Admission.Policy!;
        e.Admission.Policy = changed switch
        {
            "head" => p with { CompletionContinuity = p.CompletionContinuity! with { PreviousJobSha256 = new string('d', 64) } },
            "budget" => p with { Budget = p.Budget with { ProjectCeilingMinorUnits = p.Budget.ProjectCeilingMinorUnits + 1 } },
            _ => p with { ExpiresAtUtc = p.ExpiresAtUtc.AddDays(1) }
        };
        await Assert.ThrowsAnyAsync<Exception>(() => e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken));
        Assert.Equal(stopped, await e.Service.ReadWholeArticleJobAsync(stopped.JobId, TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    private static async Task<(ChirpEvidence Evidence, ChirpArticleAdmissionPolicy Previous, ChirpWholeArticleJob Job)>
        UntouchedCompletionCheckpointAsync(Fixture f)
    {
        var e = await WholeEvidenceAsync(f).ConfigureAwait(false); var old = e.Admission.Policy!;
        var token = TestContext.Current.CancellationToken;
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, token).ConfigureAwait(false);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), token).ConfigureAwait(false);
        job = await CheckpointRecoveryJobAsync(e.Service, job, ChirpWholeArticleState.Executing, 1, "article.request-starting", token).ConfigureAwait(false);
        job = await CheckpointRecoveryJobAsync(e.Service, job, ChirpWholeArticleState.Stopped, 1, "article.guard-rejected-reconcile", token).ConfigureAwait(false);
        e.Admission.Policy = old with
        {
            AppSha256 = new string('b', 64),
            SourceApprovalSha256 = new string('f', 64),
            CompletionContinuity = new(1, job.JobId, JsonHash(job), old)
        };
        return (e, old, job);
    }
}
