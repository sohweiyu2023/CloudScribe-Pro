using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task RepairPolicyCannotReduceItsPredecessorAttemptBaseline()
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var (e, stopped, failed, old) = await StoppedRecoveryCheckpointAsync(f);
        await AdmitRecoveryContinuationAsync(f, e, stopped, failed, old);
        var previous = e.Admission.Policy!;
        var malformed = previous with
        {
            SourceApprovalSha256 = new string('f', 64),
            PriorArticleAttemptRows = [],
            PriorSuccessorAttemptRows = [],
            RecoveryContinuation = new(1, stopped.JobId, JsonHash(stopped), failed.AuthorizationId, previous)
        };
        Assert.Throws<InvalidDataException>(() => ChirpPolicyValidation.RequireValid(malformed, e.Clock.GetUtcNow()));
        Assert.Equal(stopped, await e.Service.ReadWholeArticleJobAsync(stopped.JobId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CaptureStopWithUncertainChildCannotResumeOrReserveAgain()
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var e = await AutomaticRecoveryEvidenceAsync(f);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var runtime = new RecoveryFakeRuntime(f, e) { FailureMode = "child-uncertain" };
        await Assert.ThrowsAnyAsync<Exception>(() => e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        var stopped = await e.Service.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken);
        stopped = await CheckpointRecoveryJobAsync(e.Service, stopped, ChirpWholeArticleState.Stopped, 1,
            "article.private-capture-required", TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<Exception>(() => e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken));
        Assert.Equal(stopped, await e.Service.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_split_attempts"));
    }

    [Fact]
    public async Task ExplicitFreshReviewOfTypedCaptureStopResumesSameFailedRootWithoutAnotherRootRequest()
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var (e, stopped, _, _) = await StoppedRecoveryCheckpointAsync(f);
        stopped = await CheckpointRecoveryJobAsync(e.Service, stopped, ChirpWholeArticleState.Stopped, 1,
            "article.private-capture-required", TestContext.Current.CancellationToken);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var same = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        Assert.Equal(stopped.JobId, same.JobId); Assert.Equal(stopped.PolicySha256, same.PolicySha256);
        Assert.Equal(stopped.ExpiresAtUtc, same.ExpiresAtUtc); Assert.Equal(stopped.MaximumRecoveryRequests, same.MaximumRecoveryRequests);
        var runtime = new RecoveryFakeRuntime(f, e);
        Assert.Equal(ChirpWholeArticleState.Complete,
            (await e.Service.RunWholeArticleAsync(same.JobId, runtime, token: TestContext.Current.CancellationToken)).State);
        Assert.Equal(0, runtime.RootFailures); Assert.Equal(3, runtime.Children.Count);
    }

    [Fact]
    public async Task GenericStoppedJobStillCannotResumeUnderSamePolicy()
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var (e, stopped, _, _) = await StoppedRecoveryCheckpointAsync(f);
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken));
        Assert.Equal(stopped, await e.Service.ReadWholeArticleJobAsync(stopped.JobId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExplicitSecondBuildRepairMayPinFailedOriginalRootAndPreserveSameFamily()
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var (e, stopped, failed, old) = await StoppedRecoveryCheckpointAsync(f);
        await AdmitRecoveryContinuationAsync(f, e, stopped, failed, old);
        var p = e.Admission.Policy!;
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var first = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var secondStop = await CheckpointRecoveryJobAsync(e.Service, first, ChirpWholeArticleState.Stopped, 1,
            "article.guard-rejected-reconcile", TestContext.Current.CancellationToken);
        var root = Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        e.Admission.Policy = p with
        {
            AppSha256 = new string('c', 64),
            SourceApprovalSha256 = new string('f', 64),
            RecoveryContinuation = new(1, stopped.JobId, JsonHash(secondStop), Guid.ParseExact((string)root[0]!, "N"), p)
        };
        TestContext.Current.TestOutputHelper!.WriteLine("SYNTHETIC_RECOVERY_CHAIN_POLICY_BASE64="
            + Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(e.Admission.Policy)));
        var nextReview = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var same = await e.Service.StartWholeArticleAsync(WholeConfirmation(nextReview), TestContext.Current.CancellationToken);
        Assert.Equal(stopped.JobId, same.JobId); Assert.Equal(stopped.ExpiresAtUtc, same.ExpiresAtUtc);
        var runtime = new RecoveryFakeRuntime(f, e);
        Assert.Equal(ChirpWholeArticleState.Complete,
            (await e.Service.RunWholeArticleAsync(same.JobId, runtime, token: TestContext.Current.CancellationToken)).State);
        Assert.Equal(0, runtime.RootFailures); Assert.DoesNotContain(runtime.Children, x => x.AuthorizationId == failed.AuthorizationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairPolicyRetainsFiniteFifthHopAndRejectsReusedEarlierApproval(bool reuse)
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var (e, stopped, failed, old) = await StoppedRecoveryCheckpointAsync(f);
        await AdmitRecoveryContinuationAsync(f, e, stopped, failed, old);
        var policy = e.Admission.Policy!;
        for (int hop = 2; hop <= 5; hop++)
        {
            var next = policy with
            {
                SourceApprovalSha256 = new string((char)('0' + hop), 64),
                RecoveryContinuation = new(1, stopped.JobId, JsonHash(stopped), failed.AuthorizationId, policy)
            };
            if (hop < 5) ChirpPolicyValidation.RequireValid(next, e.Clock.GetUtcNow());
            policy = next;
        }
        if (reuse) policy = policy.RecoveryContinuation!.PreviousPolicy with { SourceApprovalSha256 = old.SourceApprovalSha256 };
        if (reuse) Assert.Throws<InvalidDataException>(() => ChirpPolicyValidation.RequireValid(policy, e.Clock.GetUtcNow()));
        else
        {
            using var shape = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(policy),
                new System.Text.Json.JsonDocumentOptions { MaxDepth = 24 });
            ChirpPolicyValidation.RequireValid(policy, e.Clock.GetUtcNow());
        }
        Assert.Equal(stopped, await e.Service.ReadWholeArticleJobAsync(stopped.JobId, TestContext.Current.CancellationToken));
    }
}
