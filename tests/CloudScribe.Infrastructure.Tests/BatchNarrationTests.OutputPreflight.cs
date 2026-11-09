#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using CloudScribe.Application.Batch;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class BatchNarrationTests
{
    [Theory]
    [InlineData("existing-file")]
    [InlineData("existing-directory")]
    [InlineData("missing-folder")]
    [InlineData("extension")]
    [InlineData("changed-tools")]
    [InlineData("missing-probe")]
    public async Task NewArticleBatchPreflightFailureLeavesNoJobOrHoldAndRetryRechecks(string failure)
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt"]);
        var profile = Profile(w.Root);
        var authority = new MemoryAuthority { Profile = profile, Consent = Consent(batch, profile, perFile: 300) };
        string tool = CreateArticleBatchTools(w.Root);
        var approved = await TrustedMediaToolsStore.CaptureAsync(tool);
        string destination = Path.Combine(w.Output, batch.Items[0].OutputFileName);
        string checkDestination = BreakArticleBatchOutput(failure, destination, tool, w.Root);
        var workflow = new FakeWorkflow(w.Output)
        {
            Preflight = (_, token) => ArticleAudioExportService.PreflightGenerationAsync(checkDestination, tool, approved, token)
        };
        workflow.Plans[batch.Items[0].DocumentId!.Value] = "plan-1";
        var narrator = new CompleteArticleBatchNarrator(new BatchAuthorityGate(authority, w.Store, new FixedClock(Now)), workflow, () => (Account, Project));
        var runner = new BatchQueueRunner(w.Store, narrator, new FixedClock(Now));
        var stopped = await runner.RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        var row = Assert.Single(stopped.Items);
        Assert.Equal(BatchItemState.Failed, row.State);
        Assert.Equal("batch.article-output-preflight-nothing-sent", row.ReasonCode);
        Assert.Null(row.JobId); Assert.Equal(0, row.ReservedMinorUnits);
        Assert.Equal(0, workflow.Started); Assert.Empty(workflow.Jobs);
        if (string.Equals(failure, "existing-file", StringComparison.Ordinal)) Assert.Equal("keep original", File.ReadAllText(destination));
        // Fix only these disposable fixtures. A prior successful rehearsal is not cached by the narrator.
        if (File.Exists(destination)) File.Delete(destination);
        if (Directory.Exists(destination)) Directory.Delete(destination);
        _ = CreateArticleBatchTools(w.Root); checkDestination = destination;
        await w.Store.SaveAsync(BatchQueueRunner.Retry(stopped, row.ItemId), Token);
        var done = await runner.RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(BatchItemState.Completed, Assert.Single(done.Items).State);
        Assert.Equal(1, workflow.Started);
    }

    [Theory]
    [InlineData(ChirpWholeArticleState.Executing)]
    [InlineData(ChirpWholeArticleState.Assembling)]
    public async Task ExistingUncertainArticleBypassesNewJobPreflightAndKeepsIdentityAndHold(ChirpWholeArticleState state)
    {
        using var w = new Workspace(); var batch = await w.CreateAsync(["a.txt"]);
        var profile = Profile(w.Root);
        var authority = new MemoryAuthority { Profile = profile, Consent = Consent(batch, profile, perFile: 300) };
        var job = new ChirpWholeArticleJob(Guid.NewGuid(), batch.Items[0].DocumentId!.Value, "plan-1", "policy", "review", 300,
            Now.AddHours(1), Now, state, 1, "article.interrupted-reconcile-no-retry");
        var workflow = new FakeWorkflow(w.Output)
        {
            Preflight = (_, _) => throw new InvalidOperationException("Existing destination must not block reconciliation."),
            StopNext = "article.interrupted-reconcile-no-retry"
        };
        workflow.Jobs[job.JobId] = job;
        batch = await w.Store.SaveAsync(batch.WithItem(batch.Items[0] with { JobId = job.JobId, ReservedMinorUnits = 300 }), Token);
        var narrator = new CompleteArticleBatchNarrator(new BatchAuthorityGate(authority, w.Store, new FixedClock(Now)), workflow, () => (Account, Project));
        var stopped = await new BatchQueueRunner(w.Store, narrator, new FixedClock(Now)).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        var item = Assert.Single(stopped.Items);
        Assert.Equal(BatchItemState.Uncertain, item.State); Assert.Equal(job.JobId, item.JobId);
        Assert.Equal(300, item.ReservedMinorUnits); Assert.Equal(0, workflow.Started);
        Assert.Single(workflow.Jobs); Assert.Empty(Directory.GetFiles(w.Output));
    }

    [Fact]
    public async Task NewArticleBatchChecksRealLinkedAncestorBeforeStartingEvenWhenWritable()
    {
        using var w = new Workspace();
        string link = Path.Combine(w.Root, "linked");
        if (!TryJunction(link, w.Output)) { Assert.Skip("Host did not permit the temporary junction; no security setting was changed."); return; }
        try
        {
            Directory.CreateDirectory(Path.Combine(w.Output, "nested"));
            string destination = Path.Combine(link, "nested", "a.mp3");
            string probe = Path.Combine(link, "nested", "probe.tmp"); File.WriteAllText(probe, "writable"); File.Delete(probe);
            var batch = await w.CreateAsync(["a.txt"]); var profile = Profile(w.Root);
            var authority = new MemoryAuthority { Profile = profile, Consent = Consent(batch, profile, perFile: 300) };
            string tool = CreateArticleBatchTools(w.Root);
            var approved = await TrustedMediaToolsStore.CaptureAsync(tool);
            var workflow = new FakeWorkflow(w.Output) { Preflight = (_, token) => ArticleAudioExportService.PreflightGenerationAsync(destination, tool, approved, token) };
            var narrator = new CompleteArticleBatchNarrator(new BatchAuthorityGate(authority, w.Store, new FixedClock(Now)), workflow, () => (Account, Project));
            var result = await new BatchQueueRunner(w.Store, narrator, new FixedClock(Now)).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
            Assert.Equal("batch.article-output-preflight-nothing-sent", Assert.Single(result.Items).ReasonCode);
            Assert.Equal(0, workflow.Started); Assert.Empty(workflow.Jobs); Assert.False(File.Exists(destination));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task ArticlePreflightRejectsLinkedMediaAncestorBeforeAnyJob()
    {
        using var w = new Workspace();
        string physical = Directory.CreateDirectory(Path.Combine(w.Root, "physical-tools")).FullName;
        string tool = CreateArticleBatchTools(physical);
        var approved = await TrustedMediaToolsStore.CaptureAsync(tool);
        string link = Path.Combine(w.Root, "linked-tools");
        if (!TryJunction(link, physical)) { Assert.Skip("Host did not permit the temporary junction; no security setting was changed."); return; }
        try
        {
            var batch = await w.CreateAsync(["a.txt"]); var profile = Profile(w.Root);
            var authority = new MemoryAuthority { Profile = profile, Consent = Consent(batch, profile, perFile: 300) };
            string aliasedTool = Path.Combine(link, "ffmpeg.exe");
            var workflow = new FakeWorkflow(w.Output)
            {
                Preflight = (output, token) => ArticleAudioExportService.PreflightGenerationAsync(output, aliasedTool,
                    approved with { EncoderPath = aliasedTool }, token)
            };
            var narrator = new CompleteArticleBatchNarrator(new BatchAuthorityGate(authority, w.Store, new FixedClock(Now)), workflow, () => (Account, Project));
            var result = await new BatchQueueRunner(w.Store, narrator, new FixedClock(Now)).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
            Assert.Equal("batch.article-output-preflight-nothing-sent", Assert.Single(result.Items).ReasonCode);
            Assert.Equal(0, workflow.Started); Assert.Empty(workflow.Jobs); Assert.Empty(Directory.GetFiles(w.Output));
        }
        finally { Directory.Delete(link); }
    }

    private static string BreakArticleBatchOutput(string failure, string destination, string tool, string root)
    {
        switch (failure)
        {
            case "existing-file": File.WriteAllText(destination, "keep original"); break;
            case "existing-directory": Directory.CreateDirectory(destination); break;
            case "missing-folder": return Path.Combine(Path.GetDirectoryName(destination)!, "missing", "a.mp3");
            case "extension": return Path.ChangeExtension(destination, ".wav");
            case "changed-tools": File.WriteAllText(tool, "changed"); break;
            case "missing-probe": File.Delete(Path.Combine(root, "ffprobe.exe")); break;
        }
        return destination;
    }

    private static string CreateArticleBatchTools(string root)
    {
        string tool = Path.Combine(root, "ffmpeg.exe");
        File.WriteAllText(tool, "synthetic encoder"); File.WriteAllText(Path.Combine(root, "ffprobe.exe"), "synthetic probe");
        return tool;
    }
}
