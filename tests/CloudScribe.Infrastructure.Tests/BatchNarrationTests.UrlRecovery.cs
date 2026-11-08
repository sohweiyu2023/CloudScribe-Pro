#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Batch;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class BatchNarrationTests
{
    [Theory]
    [InlineData(0, BatchFailurePolicy.SkipAndContinue)]
    [InlineData(1, BatchFailurePolicy.SkipAndContinue)]
    [InlineData(0, BatchFailurePolicy.StopBatch)]
    [InlineData(1, BatchFailurePolicy.StopBatch)]
    public async Task StaleUnstartedSiblingNeverBlocksSelectedExistingJob(int startedIndex, BatchFailurePolicy failurePolicy)
    {
        using var w = new Workspace();
        var settings = Settings with { SkipUrls = true, UrlTransformVersion = NarrationUrlOmission.CurrentVersion, FailurePolicy = failurePolicy };
        var batch = await w.CreateAsync(["first.txt", "second.txt"], settings);
        var library = new MemoryLibrary();
        var startedDocument = await library.CreateAsync("Started", "Existing exact saved words.", Token);
        var staleDocument = await library.CreateAsync("Stale", "Unstarted saved words.", Token);
        Guid existingJob = Guid.NewGuid();
        var started = batch.Items[startedIndex] with
        {
            DocumentId = startedDocument.Id,
            RevisionId = startedDocument.CurrentRevisionId,
            JobId = existingJob,
            State = BatchItemState.Ready,
            Engine = BatchEngines.CompleteArticle,
            NarrationTextSha256 = Sha(startedDocument.Text)
        };
        var stale = batch.Items[1 - startedIndex] with
        {
            DocumentId = staleDocument.Id,
            RevisionId = staleDocument.CurrentRevisionId,
            NarrationTextSha256 = Sha(staleDocument.Text)
        };
        batch = await w.Store.SaveAsync(batch.WithItem(started).WithItem(stale), Token);
        var profile = Profile(w.Root);
        var consent = BatchConsent.Create(Guid.NewGuid(), batch, BatchConsentSource.ReviewedInstall, profile, Account, Project,
            batch.Items.Select(x => new BatchConsentItem(x.ItemId, x.SourceSha256, x.DocumentId!.Value, x.RevisionId!.Value,
                "plan", 1, NarrationTextSha256: x.NarrationTextSha256)).ToArray(), Now);
        var authority = new MemoryAuthority { Profile = profile, Consent = consent };
        var gate = new BatchAuthorityGate(authority, w.Store, new FixedClock(Now), library, new StubCopyStore(null));
        library.Created[1] = staleDocument with { CurrentRevisionId = Guid.NewGuid(), Text = "Changed without new approval." };
        Assert.False((await gate.CheckAsync(batch, Account, Project, Token)).Allowed);
        Assert.True((await gate.CheckRunAsync(batch, Account, Project, Token)).Allowed);
        Assert.False((await gate.CheckItemAsync(batch, stale, Account, Project, Token)).Allowed);
        var fake = new FakeNarrator();
        fake.Behaviour[started.Order] = async (_, token) =>
        {
            string path = Path.Combine(w.Output, "existing.mp3");
            const string bytes = "synthetic existing output";
            await File.WriteAllTextAsync(path, bytes, token);
            return BatchNarrationOutcome.Completed(path, new FileInfo(path).Length, Sha(bytes), 1, 1);
        };
        var router = new RoutingBatchNarrator(gate, () => (Account, Project),
            new Dictionary<string, IBatchNarrator>(StringComparer.Ordinal) { [BatchEngines.CompleteArticle] = fake });
        var result = await new BatchQueueRunner(w.Store, router, new FixedClock(Now)).RunAsync(batch.BatchId, settings.Sha256(), cancellationToken: Token);
        var call = Assert.Single(fake.Calls);
        Assert.Equal(started.Order, call.Order); Assert.Equal(existingJob, call.JobIdAtStart);
        Assert.Equal(0, fake.RecordJobCalls);
        Assert.Equal(BatchItemState.Completed, result.Items.Single(x => x.ItemId == started.ItemId).State);
        var blocked = result.Items.Single(x => x.ItemId == stale.ItemId);
        Assert.Null(blocked.JobId); Assert.Equal("batch.narration-changed", blocked.ReasonCode);
    }
}
