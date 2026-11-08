#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using CloudScribe.Application.Batch;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Files;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class BatchNarrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Track 2.txt", "Track 10.txt")]
    [InlineData("track 2.txt", "Track 3.txt")]
    [InlineData("第2章.txt", "第10章.txt")]
    [InlineData("Part ２.txt", "Part １０.txt")]
    [InlineData("01 Preface.txt", "02 I AM.txt")]
    [InlineData("a.txt", "a1.txt")]
    public void NaturalOrderReadsNumbersAsPeopleDo(string first, string second)
    {
        Assert.True(BatchNaturalOrder.Instance.Compare(first, second) < 0);
        Assert.True(BatchNaturalOrder.Instance.Compare(second, first) > 0);
        Assert.Equal(0, BatchNaturalOrder.Instance.Compare(first, first));
    }

    [Fact]
    public void PlannerOrdersExcludesAndNamesEveryFileOnce()
    {
        string folder = Path.Combine(Path.GetTempPath(), "cloudscribe-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "Existing.mp3"), "keep me");
            var files = new[]
            {
                new BatchSourceFile("10 Ten.txt", "h10", 5, "ten"),
                new BatchSourceFile("2 Two.txt", "h2", 5, "two"),
                new BatchSourceFile("Empty.txt", "he", 0, "   \n"),
                new BatchSourceFile("Copy of two.txt", "h2", 5, "two"),
                new BatchSourceFile("GBK.txt", "hg", 5, null, BatchExclusion.NotUnicodeText),
                new BatchSourceFile("Existing.txt", "hx", 5, "x"),
                new BatchSourceFile("existing.TXT", "hy", 5, "y"),
                new BatchSourceFile("無我 Anatta.txt", "hz", 9, "無我"),
            };
            // Windows folders are case-insensitive: model that, whatever the test host.
            var items = BatchPlanner.Plan(files, t => t + ".mp3", n => Directory.EnumerateFiles(folder).Any(p => string.Equals(Path.GetFileName(p), n, StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(["2 Two.txt", "10 Ten.txt"], items.Take(2).Select(x => x.SourceFileName), StringComparer.Ordinal);
            Assert.Equal(Enumerable.Range(1, files.Length), items.Select(x => x.Order));
            Assert.Equal(BatchExclusion.Empty, items.Single(x => string.Equals(x.SourceFileName, "Empty.txt", StringComparison.Ordinal)).Exclusion);
            var copy = items.Single(x => string.Equals(x.SourceFileName, "Copy of two.txt", StringComparison.Ordinal));
            Assert.True(copy.IsIncluded); // Identical text is kept as its own track and only noted.
            Assert.Equal(items.Single(x => string.Equals(x.SourceFileName, "2 Two.txt", StringComparison.Ordinal)).Order, copy.SameTextAs);
            Assert.Equal(BatchExclusion.NotUnicodeText, items.Single(x => string.Equals(x.SourceFileName, "GBK.txt", StringComparison.Ordinal)).Exclusion);
            var included = items.Where(x => x.IsIncluded).Select(x => x.OutputFileName).ToArray();
            Assert.Equal(included.Length, included.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Contains("Existing (2).mp3", included, StringComparer.Ordinal);
            Assert.Contains("existing (3).mp3", included, StringComparer.Ordinal);
            Assert.Contains("無我 Anatta.mp3", included, StringComparer.Ordinal);
            Assert.All(items.Where(x => !x.IsIncluded), x => Assert.Equal(string.Empty, x.OutputFileName));
            Assert.Equal(2, items.Single(x => string.Equals(x.SourceFileName, "無我 Anatta.txt", StringComparison.Ordinal)).Characters);
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(folder, "Existing.mp3")));

            var moved = BatchPlanner.AssignOutputNames(BatchPlanner.Move(items, items[1].ItemId, -1), t => t + ".mp3", _ => false);
            Assert.Equal("10 Ten.txt", moved[0].SourceFileName);
            Assert.Equal(1, moved[0].Order);
            var removed = BatchPlanner.Remove(moved, moved[0].ItemId);
            Assert.Equal(BatchExclusion.RemovedByUser, removed[0].Exclusion);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task FourteenFileBatchRunsInOrderOnceEachAndSummarises()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(FourteenTracks);
        var narrator = new FakeNarrator(); var log = new ListLogger();
        var result = await new BatchQueueRunner(w.Store, narrator, TimeProvider.System, log).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(BatchQueueState.Finished, result.State);
        Assert.Equal(14, result.CompletedCount);
        Assert.Equal(Enumerable.Range(1, 14), narrator.Calls.Select(x => x.Order));
        Assert.Equal(14, narrator.RecordJobCalls);
        Assert.All(result.Items, x => Assert.True(File.Exists(x.OutputPath)));
        Assert.Equal(14 * 120, result.EstimatedMaximumMinorUnits);
        Assert.Equal(14 * 130, result.ReservedMinorUnits);
        Assert.NotNull(result.FinishedUtc);
        Assert.Contains(log.Messages, m => m.Contains("not a provider bill", StringComparison.Ordinal) && m.Contains("14 completed", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Messages, m => FourteenTracks.Any(t => m.Contains(BatchPlanner.TitleOf(t), StringComparison.Ordinal)));

        var again = await new BatchQueueRunner(w.Store, narrator, TimeProvider.System).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(14, narrator.Calls.Count); // A finished batch is never narrated again.
        Assert.Equal(BatchQueueState.Finished, again.State);

        var (playlist, summary) = BatchManifestWriter.Write(result);
        var lines = File.ReadAllLines(playlist);
        Assert.Equal("#EXTM3U", lines[0]);
        Assert.Equal("01 Preface.mp3", lines[2]);
        Assert.Equal("14 Glossary.mp3", lines[^1]);
        Assert.Contains("\"01 Preface.txt\"", File.ReadAllText(summary), StringComparison.Ordinal);
        Assert.NotEqual(playlist, BatchManifestWriter.Write(result).Playlist, StringComparer.Ordinal); // never overwrites
    }

    [Fact]
    public async Task LargeBatchHasNoArbitraryFileLimit()
    {
        using var w = new Workspace();
        var names = Enumerable.Range(1, 250).Select(i => $"Chapter {i}.txt").Reverse().ToArray();
        var batch = await w.CreateAsync(names);
        var narrator = new FakeNarrator();
        var result = await new BatchQueueRunner(w.Store, narrator, TimeProvider.System).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(250, result.CompletedCount);
        Assert.Equal("Chapter 1.txt", result.Items[0].SourceFileName);
        Assert.Equal("Chapter 250.txt", result.Items[^1].SourceFileName);
    }

    [Fact]
    public async Task RestartAfterCrashContinuesTheSavedJobAndNeverStartsASecondOne()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt", "b.txt"]);
        var savedJob = Guid.NewGuid();
        // Simulate a process that died while file 1 was running with a recorded job.
        batch = await w.Store.SaveAsync(batch.WithItem(batch.Items[0] with { State = BatchItemState.Running, JobId = savedJob }) with { State = BatchQueueState.Running }, Token);
        var narrator = new FakeNarrator();
        var result = await new BatchQueueRunner(w.Store, narrator, TimeProvider.System).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal((1, (Guid?)savedJob), narrator.Calls.First());
        Assert.Equal(1, narrator.RecordJobCalls); // only file 2 started a job
        Assert.Equal(savedJob, result.Items[0].JobId);
        Assert.Equal(BatchQueueState.Finished, result.State);
    }

    [Fact]
    public async Task UncertainOutcomeIsNeverResubmittedUntilTheUserAsksToCheckAgain()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt", "b.txt", "c.txt"]);
        var narrator = new FakeNarrator();
        narrator.Behaviour[2] = (_, _) => Task.FromResult(new BatchNarrationOutcome(BatchOutcomeKind.Uncertain, "article.provider-outcome-uncertain-no-retry", ReservedMinorUnits: 40));
        var runner = new BatchQueueRunner(w.Store, narrator, TimeProvider.System);
        var first = await runner.RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(BatchItemState.Uncertain, first.Items[1].State);
        Assert.Equal(BatchQueueState.Finished, first.State);
        Assert.Equal(2, first.CompletedCount);
        Assert.Equal(40, first.Items[1].ReservedMinorUnits);

        var second = await runner.RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(3, narrator.Calls.Count); // finished: nothing re-sent

        narrator.Behaviour.Remove(2);
        var reopened = await w.Store.SaveAsync(BatchQueueRunner.CheckAgain(second, second.Items[1].ItemId), Token);
        var third = await runner.RunAsync(reopened.BatchId, Settings.Sha256(), cancellationToken: Token);
        var check = narrator.Calls.Last();
        Assert.Equal(2, check.Order);
        Assert.Equal(first.Items[1].JobId, check.JobIdAtStart); // reconciles the same saved job
        Assert.Equal(3, narrator.RecordJobCalls);
        Assert.Equal(3, third.CompletedCount);
    }

    [Fact]
    public async Task FailedFileDoesNotEraseOthersAndRetryReusesCompletedWork()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt", "b.txt", "c.txt", "d.txt"]);
        var narrator = new FakeNarrator();
        narrator.Behaviour[3] = (_, _) => Task.FromResult(new BatchNarrationOutcome(BatchOutcomeKind.Failed, "article.provider-rejected-no-retry"));
        var runner = new BatchQueueRunner(w.Store, narrator, TimeProvider.System);
        var first = await runner.RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal([BatchItemState.Completed, BatchItemState.Completed, BatchItemState.Failed, BatchItemState.Completed], first.Items.Select(x => x.State));
        Assert.All(first.Items.Where(x => x.State == BatchItemState.Completed), x => Assert.True(File.Exists(x.OutputPath)));

        narrator.Behaviour.Remove(3);
        var retried = await w.Store.SaveAsync(BatchQueueRunner.Retry(first, first.Items[2].ItemId), Token);
        var second = await runner.RunAsync(retried.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(5, narrator.Calls.Count); // only file 3 again
        Assert.Equal(first.Items[2].JobId, narrator.Calls.Last().JobIdAtStart);
        Assert.Equal(2, second.Items[2].Attempts);
        Assert.Equal(4, second.CompletedCount);
    }

    [Fact]
    public async Task StopBatchPolicyPausesAtTheFirstFailure()
    {
        using var w = new Workspace();
        var stop = Settings with { FailurePolicy = BatchFailurePolicy.StopBatch };
        var batch = await w.CreateAsync(["a.txt", "b.txt", "c.txt"], stop);
        var narrator = new FakeNarrator();
        narrator.Behaviour[2] = (_, _) => Task.FromResult(new BatchNarrationOutcome(BatchOutcomeKind.Failed, "article.guard-rejected-reconcile"));
        var result = await new BatchQueueRunner(w.Store, narrator, TimeProvider.System).RunAsync(batch.BatchId, stop.Sha256(), cancellationToken: Token);
        Assert.Equal(BatchQueueState.Paused, result.State);
        Assert.Equal("batch.stopped-on-failure", result.ReasonCode);
        Assert.Equal(BatchItemState.Ready, result.Items[2].State);
        Assert.Equal(2, narrator.Calls.Count);
    }

    [Fact]
    public async Task DuplicateStartIsRefusedWhileARunOwnsTheBatch()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt", "b.txt"]);
        var narrator = new FakeNarrator { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var first = new BatchQueueRunner(w.Store, narrator, TimeProvider.System).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        while (narrator.Calls.IsEmpty) await Task.Delay(10, Token);
        Assert.True(BatchQueueRunner.IsRunning(batch.BatchId));
        await Assert.ThrowsAsync<BatchAlreadyRunningException>(() =>
            new BatchQueueRunner(w.Store, narrator, TimeProvider.System).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token));
        using (var otherProcess = w.Store.TryAcquireRunLock(batch.BatchId)) Assert.Null(otherProcess);
        narrator.Gate.SetResult();
        Assert.Equal(BatchQueueState.Finished, (await first).State);
        Assert.Single(narrator.Calls, x => x.Order == 1);
    }

    [Fact]
    public async Task ChangedSettingsOrMissingAuthoritySendNothing()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt"]);
        var narrator = new FakeNarrator();
        var runner = new BatchQueueRunner(w.Store, narrator, TimeProvider.System);
        await Assert.ThrowsAsync<BatchSettingsChangedException>(() =>
            runner.RunAsync(batch.BatchId, (Settings with { VoiceStableId = "other" }).Sha256(), cancellationToken: Token));
        narrator.Authorized = false;
        var error = await Assert.ThrowsAsync<BatchNotAuthorizedException>(() => runner.RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token));
        Assert.Contains("No batch authority", error.Message, StringComparison.Ordinal);
        Assert.Empty(narrator.Calls);
        var saved = await w.Store.LoadAsync(batch.BatchId, Token);
        Assert.Equal(BatchQueueState.Paused, saved!.State);
        Assert.Equal("batch.not-authorized", saved.ReasonCode);
    }

    [Fact]
    public async Task PauseStopsAtASafeBoundaryAndResumeFinishesTheRest()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt", "b.txt", "c.txt"]);
        var narrator = new FakeNarrator();
        var runner = new BatchQueueRunner(w.Store, narrator, TimeProvider.System);
        narrator.Behaviour[1] = async (item, token) =>
        {
            runner.RequestPause();
            string path = Path.Combine(w.Output, item.OutputFileName);
            await File.WriteAllTextAsync(path, "one", token);
            return BatchNarrationOutcome.Completed(path, 3, "x");
        };
        var paused = await runner.RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(BatchQueueState.Paused, paused.State);
        Assert.Equal([BatchItemState.Completed, BatchItemState.Ready, BatchItemState.Ready], paused.Items.Select(x => x.State));
        var resumed = await new BatchQueueRunner(w.Store, narrator, TimeProvider.System).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(BatchQueueState.Finished, resumed.State);
        Assert.Equal(3, narrator.Calls.Count);
    }

    [Fact]
    public async Task CancellationDuringAFileKeepsItsJobForReconciliation()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt", "b.txt"]);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var narrator = new FakeNarrator();
        narrator.Behaviour[1] = async (_, token) => { await cancel.CancelAsync(); token.ThrowIfCancellationRequested(); return null!; };
        var result = await new BatchQueueRunner(w.Store, narrator, TimeProvider.System).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: cancel.Token);
        Assert.Equal(BatchQueueState.Paused, result.State);
        Assert.Equal(BatchItemState.Ready, result.Items[0].State);
        Assert.NotNull(result.Items[0].JobId);
    }

    [Fact]
    public async Task UnexpectedErrorsBeforeAndAfterAJobAreClassifiedSafely()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt", "b.txt"]);
        var narrator = new FakeNarrator();
        narrator.Behaviour[1] = (_, _) => throw new IOException("disk");
        var result = await new BatchQueueRunner(w.Store, narrator, TimeProvider.System).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(BatchItemState.Uncertain, result.Items[0].State); // a job existed: never assume nothing was sent
        Assert.Equal("batch.narration-failed-after-start-reconcile", result.Items[0].ReasonCode);

        var batch2 = await w.CreateAsync(["c.txt"]);
        var early = new ThrowingNarrator();
        var result2 = await new BatchQueueRunner(w.Store, early, TimeProvider.System).RunAsync(batch2.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(BatchItemState.Failed, result2.Items[0].State);
        Assert.Equal("batch.narration-failed-before-start", result2.Items[0].ReasonCode);
    }

    [Fact]
    public async Task MissingCompletedOutputIsReportedNotSilentlyRegenerated()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt", "b.txt"]);
        var narrator = new FakeNarrator();
        var first = await new BatchQueueRunner(w.Store, narrator, TimeProvider.System).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        File.Delete(first.Items[0].OutputPath!);
        var reopened = await w.Store.SaveAsync(BatchQueueRunner.Skip(first, first.Items[1].ItemId) with { State = BatchQueueState.Paused }, Token);
        var second = await new BatchQueueRunner(w.Store, narrator, TimeProvider.System).RunAsync(reopened.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(BatchItemState.Failed, second.Items[0].State);
        Assert.Equal("batch.output-missing-or-changed", second.Items[0].ReasonCode);
        Assert.Equal(2, narrator.Calls.Count);
    }

    [Fact]
    public async Task StoreSavesAtomicallyRefusesStaleWritesAndRecoversFromACorruptFile()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt"]);
        var stale = batch;
        var newer = await w.Store.SaveAsync(batch with { Name = "Renamed" }, Token);
        Assert.Equal(batch.Revision + 1, newer.Revision);
        await Assert.ThrowsAsync<BatchConcurrencyException>(() => w.Store.SaveAsync(stale with { Name = "Lost update" }, Token));
        string path = Path.Combine(w.Store.Directory, batch.BatchId.ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{ truncated", Token); // interrupted write
        var recovered = await w.Store.LoadAsync(batch.BatchId, Token);
        Assert.NotNull(recovered);
        Assert.Equal(batch.BatchId, recovered.BatchId);
        await File.WriteAllTextAsync(path + ".tmp", "partial", Token);
        Assert.Single(await w.Store.ListAsync(Token));
        Assert.DoesNotContain("Synthetic text", await File.ReadAllTextAsync(path + ".bak", Token), StringComparison.Ordinal); // no article text stored
    }

    [Fact]
    public async Task ImporterUsesTheNormalDecoderAndAddsEachDocumentOnce()
    {
        using var w = new Workspace();
        var library = new MemoryLibrary();
        var importer = new BatchDocumentImporter(new BoundedLocalDocumentImporter(), new DocumentPreprocessor(), library, w.Store);
        static MemoryStream Bytes(byte[] value) => new(value);
        var utf8 = await importer.ReadAsync("無我.txt", Bytes([0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("無我\r\n第二行")]), 1 << 20, Token);
        var utf16 = await importer.ReadAsync("u16.txt", Bytes([0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes("Hello")]), 1 << 20, Token);
        var gbk = await importer.ReadAsync("gbk.txt", Bytes([0xCE, 0xDE, 0xCE, 0xD2]), 1 << 20, Token);
        var empty = await importer.ReadAsync("empty.txt", Bytes([]), 1 << 20, Token);
        var large = await importer.ReadAsync("large.txt", Bytes(new byte[2048]), 1024, Token);
        var docx = await importer.ReadAsync("notes.docx", Bytes([1, 2, 3]), 1 << 20, Token);
        Assert.Equal("無我\r\n第二行", utf8.Text);
        Assert.Equal("Hello", utf16.Text);
        Assert.Equal(BatchExclusion.NotUnicodeText, gbk.Problem);
        Assert.Equal(BatchExclusion.Empty, empty.Problem);
        Assert.Equal(BatchExclusion.TooLarge, large.Problem);
        Assert.Equal(BatchExclusion.Unsupported, docx.Problem);

        var items = BatchPlanner.Plan([utf8, utf16, gbk], t => t + ".mp3", _ => false);
        var batch = await w.Store.SaveAsync(new NarrationBatch(NarrationBatch.CurrentVersion, Guid.NewGuid(), "b", 0, BatchQueueState.Ready,
            Settings, Settings.Sha256(), w.Output, "USD", 2, items, DateTimeOffset.UnixEpoch), Token);
        var texts = items.Where(x => string.Equals(x.SourceFileName, "無我.txt", StringComparison.Ordinal)).ToDictionary(x => x.ItemId, _ => utf8.Text!);
        var partial = await importer.ImportAsync(batch, texts, Token); // the second file's text was lost (app closed)
        Assert.Single(library.Created);
        Assert.Equal("batch.import-interrupted-add-again", partial.Items.Single(x => string.Equals(x.SourceFileName, "u16.txt", StringComparison.Ordinal)).ReasonCode);
        Assert.Equal("無我\n第二行", library.Created[0].Text); // same line-ending normalisation as a single import
        var again = await importer.ImportAsync(partial, texts, Token);
        Assert.Single(library.Created); // never imported twice
        Assert.Equal(partial.Items.Select(x => x.DocumentId), again.Items.Select(x => x.DocumentId));
    }

    private sealed class ThrowingNarrator : IBatchNarrator
    {
        public Task<(bool Authorized, string Explanation)> CheckAuthorityAsync(NarrationBatch queue, CancellationToken cancellationToken = default) => Task.FromResult((true, "ok"));
        public Task<BatchNarrationOutcome> NarrateAsync(NarrationBatch queue, BatchItem item, Func<Guid, Task> recordJob, IProgress<BatchItemProgress>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Synthetic failure before any job.");
        public Task<bool> VerifyOutputAsync(BatchItem item, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
