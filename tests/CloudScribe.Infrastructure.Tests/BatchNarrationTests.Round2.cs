#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

/// <summary>Second review round: ordering, numbering, recovery, verification, Long Audio narration and approvals.</summary>
public sealed partial class BatchNarrationTests
{
    private static BatchSourceFile Source(string name, string text) =>
        new(name, Sha(text), Encoding.UTF8.GetByteCount(text), text);

    [Fact]
    public void AddingFilesKeepsTheArrangedOrderAndNumbersEachTrackOnce()
    {
        var first = BatchPlanner.Plan([Source("b.txt", "B"), Source("a.txt", "A"), Source("10 Ten.txt", "T")], t => t + ".mp3", _ => false, numberTracks: true);
        Assert.Equal(["10 Ten.txt", "a.txt", "b.txt"], first.Select(x => x.SourceFileName), StringComparer.Ordinal);
        Assert.Equal(["10 Ten.mp3", "02 a.mp3", "03 b.mp3"], first.Select(x => x.OutputFileName), StringComparer.Ordinal); // never "01 10 Ten"
        var arranged = BatchPlanner.AssignOutputNames(BatchPlanner.Move(BatchPlanner.Move(first, first[2].ItemId, -1), first[2].ItemId, -1), t => t + ".mp3", _ => false, numberTracks: true);
        Assert.Equal(["b.txt", "10 Ten.txt", "a.txt"], arranged.Select(x => x.SourceFileName), StringComparer.Ordinal);
        var more = BatchPlanner.Append(arranged, [Source("d.txt", "D"), Source("c.txt", "A")], t => t + ".mp3", _ => false, numberTracks: true);
        Assert.Equal(["b.txt", "10 Ten.txt", "a.txt", "c.txt", "d.txt"], more.Select(x => x.SourceFileName), StringComparer.Ordinal);
        Assert.Equal(["01 b.mp3", "10 Ten.mp3", "03 a.mp3", "04 c.mp3", "05 d.mp3"], more.Select(x => x.OutputFileName), StringComparer.Ordinal);
        Assert.Equal(3, more[3].SameTextAs); // same text as "a.txt" (track 3): kept, only noted
        Assert.All(more, x => Assert.True(x.IsIncluded));
        Assert.True(BatchPlanner.StartsWithTrackNumber("01 Preface"));
        Assert.True(BatchPlanner.StartsWithTrackNumber("3.Intro"));
        Assert.False(BatchPlanner.StartsWithTrackNumber("Chapter 1"));
    }

    [Fact]
    public void ClashingNamesAreReportedAndNeverReuseAnExistingFile()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Preface.mp3", "Preface (2).mp3" };
        var items = BatchPlanner.Plan([Source("Preface.txt", "P"), Source("Glossary.txt", "G")], t => t + ".mp3", existing.Contains);
        Assert.Equal("Preface (3).mp3", items.Single(x => string.Equals(x.SourceFileName, "Preface.txt", StringComparison.Ordinal)).OutputFileName);
        var renamed = BatchPlanner.Renamed(items, t => t + ".mp3");
        Assert.Equal(["Preface.txt"], renamed.Select(x => x.SourceFileName), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ADamagedMainFileIsKeptAsideAndTheBatchSavesAndResumesFromItsBackup()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt", "b.txt"]);
        batch = await w.Store.SaveAsync(batch with { Name = "second save" }, Token); // creates the .bak
        string path = Path.Combine(w.Store.Directory, batch.BatchId.ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{ \"Version\": 1, truncated", Token);
        var loaded = await w.Store.LoadAsync(batch.BatchId, Token);
        Assert.NotNull(loaded);
        var resumed = await new BatchQueueRunner(w.Store, new FakeNarrator(), TimeProvider.System).RunAsync(loaded.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(BatchQueueState.Finished, resumed.State);
        Assert.Equal(2, resumed.CompletedCount);
        string damaged = Assert.Single(Directory.GetFiles(w.Store.Directory, "*.damaged-*"));
        Assert.Contains("truncated", await File.ReadAllTextAsync(damaged, Token), StringComparison.Ordinal); // preserved, not deleted
        Assert.NotNull(await w.Store.LoadAsync(batch.BatchId, Token));
    }

    [Fact]
    public async Task ADamagedFileWithAStaleBackupIsNeverOverwritten()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt"]);
        var stale = await w.Store.SaveAsync(batch with { Name = "two" }, Token);
        _ = await w.Store.SaveAsync(stale with { Name = "three" }, Token);
        string path = Path.Combine(w.Store.Directory, batch.BatchId.ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "garbage", Token);
        await Assert.ThrowsAsync<BatchConcurrencyException>(() => w.Store.SaveAsync(batch, Token)); // an older copy than the backup
        Assert.Equal("garbage", await File.ReadAllTextAsync(path, Token));
    }

    [Fact]
    public async Task SuccessIsOnlyDeclaredForAnOutputThatVerifies()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt", "b.txt"]);
        var narrator = new FakeNarrator();
        narrator.MissingOutputs.Add("a.mp3"); // the engine reports success, but the file does not check out
        var result = await new BatchQueueRunner(w.Store, narrator, TimeProvider.System).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(BatchItemState.Failed, result.Items[0].State);
        Assert.Equal("batch.output-not-verified", result.Items[0].ReasonCode);
        Assert.NotNull(result.Items[0].JobId); // Retry continues the same job
        Assert.Equal(BatchItemState.Completed, result.Items[1].State);
    }

    [Fact]
    public async Task ResumeAfterStopNowContinuesTheSameJobWithAFreshRun()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt", "b.txt"]);
        var narrator = new FakeNarrator();
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Token))
        {
            narrator.Behaviour[1] = async (_, token) => { await stop.CancelAsync(); token.ThrowIfCancellationRequested(); return null!; };
            var stopped = await new BatchQueueRunner(w.Store, narrator, TimeProvider.System).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: stop.Token);
            Assert.Equal(BatchQueueState.Paused, stopped.State);
        }
        narrator.Behaviour.Remove(1);
        var resumed = await new BatchQueueRunner(w.Store, narrator, TimeProvider.System).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(BatchQueueState.Finished, resumed.State);
        Assert.Equal(2, narrator.RecordJobCalls); // file 1 kept its job; only file 2 created one
    }

    [Fact]
    public void LedgerCountsApprovedFilesOfOtherBatchesThatHaveNotStartedWhileTheirApprovalIsValid()
    {
        var item = new BatchItem(Guid.NewGuid(), 1, "a.txt", "s", 1, 1, "a.mp3", BatchItemState.Ready, BatchExclusion.None, Guid.NewGuid(), Guid.NewGuid());
        var batch = new NarrationBatch(2, Guid.NewGuid(), "b", 1, BatchQueueState.Ready, Settings, Settings.Sha256(), "/tmp", "USD", 2, [item], Now);
        var consent = new BatchConsent(2, Guid.NewGuid(), batch.BatchId, BatchConsentSource.InApp, "p", "s", Account, Project, "USD", 2,
            [new(item.ItemId, "s", item.DocumentId!.Value, item.RevisionId!.Value, "plan", 300)], 300, Now, Now.AddDays(2), "h");
        Assert.Equal(300, BatchSpendLedger.Committed([batch], [consent], Now, 30));
        Assert.Equal(0, BatchSpendLedger.Committed([batch], [consent], Now.AddDays(3), 30)); // approval expired, never started
        Assert.Equal(0, BatchSpendLedger.Committed([batch with { State = BatchQueueState.Finished }], [consent], Now, 30));
    }

    [Fact]
    public async Task EvaluatorChecksEachFilesEngineAndLongAudioApproval()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt"], Settings with { Engine = BatchEngines.LongAudioFirst });
        var profile = Profile(w.Root);
        var x = batch.Items[0];
        BatchConsent With(BatchConsentItem item) => BatchConsent.Create(Guid.NewGuid(), batch, BatchConsentSource.ReviewedInstall, profile, Account, Project, [item], Now);
        var la = new BatchLongAudioApproval(Guid.NewGuid(), "gs://b/x/" + Guid.NewGuid().ToString("N") + ".mp3", new string('a', 64), 10, 10, 4096, 24000, 1, false);
        var good = new BatchConsentItem(x.ItemId, x.SourceSha256, x.DocumentId!.Value, x.RevisionId!.Value, la.RequestBindingSha256, 10, BatchEngines.LongAudio, la);
        Assert.Equal("batch.authorized", Code(profile, With(good), batch));
        var restricted = profile with { AllowedEngines = [BatchEngines.CompleteArticle] };
        Assert.Equal("batch.consent-engine", Code(restricted, BatchConsent.Create(Guid.NewGuid(), batch, BatchConsentSource.ReviewedInstall, restricted, Account, Project, [good], Now), batch));
        Assert.Equal("batch.consent-invalid", Code(profile, With(good with { LongAudio = null }), batch));
        Assert.Equal("batch.consent-invalid", Code(profile, With(good with { MaximumMinorUnits = 5 }), batch));
        Assert.Equal("batch.consent-invalid", Code(profile, With(good with { PlanSha256 = new string('b', 64) }), batch));
        var started = batch.WithItem(x with { JobId = Guid.NewGuid(), Engine = BatchEngines.CompleteArticle });
        Assert.Equal("batch.consent-engine", Code(profile, With(good), started)); // never switch engines after a start
    }

    [Fact]
    public async Task LongAudioNarratorSendsNothingUnlessTheServiceReviewMatchesTheApproval()
    {
        using var w = new Workspace();
        var (batch, authority, workflow) = await LongAudioSetupAsync(w, ["01 序.txt", "02 Intro.txt", "03 第三章.txt"]);
        workflow.Unadmitted.Add(batch.Items[1].DocumentId!.Value);
        workflow.Overpriced.Add(batch.Items[2].DocumentId!.Value);
        var narrator = new LongAudioBatchNarrator(new BatchAuthorityGate(authority, w.Store, new FixedClock(Now)), workflow);
        var router = new RoutingBatchNarrator(new BatchAuthorityGate(authority, w.Store, new FixedClock(Now)), () => (Account, Project),
            new Dictionary<string, IBatchNarrator>(StringComparer.Ordinal) { [BatchEngines.LongAudio] = narrator });
        var result = await new BatchQueueRunner(w.Store, router, new FixedClock(Now)).RunAsync(batch.BatchId, batch.SettingsSha256, cancellationToken: Token);
        Assert.Equal(BatchItemState.Completed, result.Items[0].State);
        Assert.True(await BatchOutputFiles.MatchesAsync(result.Items[0].OutputPath, result.Items[0].OutputSha256, result.Items[0].OutputBytes, Token));
        Assert.Equal("batch.long-audio-not-admitted-nothing-sent", result.Items[1].ReasonCode);
        Assert.Null(result.Items[1].JobId);
        Assert.Equal("batch.file-over-approved-maximum-nothing-sent", result.Items[2].ReasonCode);
        Assert.Null(result.Items[2].JobId);
        Assert.Equal(1, workflow.Runs); // only the matching file was ever run
    }

    [Fact]
    public async Task LongAudioReusesSavedSpeechWhenOnlyTheLocalCopyFailed()
    {
        using var w = new Workspace();
        var (batch, authority, workflow) = await LongAudioSetupAsync(w, ["01 Preface.txt"]);
        string destination = Path.Combine(w.Output, batch.Items[0].OutputFileName);
        await File.WriteAllTextAsync(destination, "someone else's file", Token); // a clash appears after planning
        var gate = new BatchAuthorityGate(authority, w.Store, new FixedClock(Now));
        var narrator = new LongAudioBatchNarrator(gate, workflow);
        var router = new RoutingBatchNarrator(gate, () => (Account, Project), new Dictionary<string, IBatchNarrator>(StringComparer.Ordinal) { [BatchEngines.LongAudio] = narrator });
        var runner = new BatchQueueRunner(w.Store, router, new FixedClock(Now));
        var first = await runner.RunAsync(batch.BatchId, batch.SettingsSha256, cancellationToken: Token);
        Assert.Equal("batch.local-copy-failed-speech-kept", first.Items[0].ReasonCode);
        Assert.Equal("someone else's file", await File.ReadAllTextAsync(destination, Token)); // never overwritten
        File.Move(destination, destination + ".moved-by-user");
        var retried = await w.Store.SaveAsync(BatchQueueRunner.Retry(first, first.Items[0].ItemId), Token);
        var second = await runner.RunAsync(retried.BatchId, batch.SettingsSha256, cancellationToken: Token);
        Assert.Equal(BatchItemState.Completed, second.Items[0].State);
        Assert.Equal(1, workflow.Runs); // no new speech: the saved, verified MP3 was copied again
    }

    [Fact]
    public async Task LongAudioNeverResubmitsAnOperationThatNeedsReconciliation()
    {
        using var w = new Workspace();
        var (batch, authority, workflow) = await LongAudioSetupAsync(w, ["01 a.txt"]);
        workflow.NeedsReconciliation = true;
        var gate = new BatchAuthorityGate(authority, w.Store, new FixedClock(Now));
        var router = new RoutingBatchNarrator(gate, () => (Account, Project), new Dictionary<string, IBatchNarrator>(StringComparer.Ordinal) { [BatchEngines.LongAudio] = new LongAudioBatchNarrator(gate, workflow) });
        var result = await new BatchQueueRunner(w.Store, router, new FixedClock(Now)).RunAsync(batch.BatchId, batch.SettingsSha256, cancellationToken: Token);
        Assert.Equal(BatchItemState.Uncertain, result.Items[0].State);
        Assert.Equal("batch.long-audio-needs-reconciliation", result.Items[0].ReasonCode);
        Assert.Equal(0, workflow.Runs);
        workflow.NeedsReconciliation = false; workflow.StopWith = LongAudioWorkflowStage.Failed;
        var again = await w.Store.SaveAsync(BatchQueueRunner.CheckAgain(result, result.Items[0].ItemId), Token);
        var checkedAgain = await new BatchQueueRunner(w.Store, router, new FixedClock(Now)).RunAsync(again.BatchId, batch.SettingsSha256, cancellationToken: Token);
        Assert.Equal("batch.long-audio-failed-check-again", checkedAgain.Items[0].ReasonCode);
        Assert.Equal(result.Items[0].JobId, checkedAgain.Items[0].JobId); // the same attempt, never a new one
    }

    [Fact]
    public async Task VerifiedCopiesNeverOverwriteAndReuseIdenticalFiles()
    {
        using var w = new Workspace();
        string source = Path.Combine(w.Root, "speech.mp3");
        await File.WriteAllTextAsync(source, "verified speech", Token);
        string sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(source, Token)));
        long bytes = new FileInfo(source).Length;
        string destination = Path.Combine(w.Output, "01 a.mp3");
        await BatchOutputFiles.CopyVerifiedAsync(source, sha, bytes, destination, Token);
        await BatchOutputFiles.CopyVerifiedAsync(source, sha, bytes, destination, Token); // identical: reused
        await File.WriteAllTextAsync(source, "changed!", Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => BatchOutputFiles.CopyVerifiedAsync(source, sha, bytes, Path.Combine(w.Output, "02.mp3"), Token));
        Assert.False(File.Exists(Path.Combine(w.Output, "02.mp3")));
        Assert.Empty(Directory.GetFiles(w.Output, "*.partial"));
    }

    [Fact]
    public void LongAudioPlannerUsesTheInstalledScopeAndRefusesWhatItCannotTake()
    {
        var scope = new GoogleLongAudioBudgetScope(1, new string('a', 64), new string('b', 64), "123456", "cred", "cmn-CN-Chirp3-HD-Alnilam", "cmn-CN",
            "LINEAR16", "private-bucket", 100_000, 200, 30m, "fixture", new Dictionary<string, long>(StringComparer.Ordinal));
        var context = new BatchLongAudioPlanner.Context(new InstalledGoogleLongAudioContext("p", new string('c', 64), "s", new string('d', 64), "l", "o", "u", scope), 5000, 100_000, null);
        var settings = Settings with { VoiceStableId = scope.VoiceName, Engine = BatchEngines.LongAudioFirst };
        var batch = new NarrationBatch(2, Guid.NewGuid(), "b", 1, BatchQueueState.Ready, settings, settings.Sha256(), "out", "USD", 2, [], Now);
        string text = string.Concat(Enumerable.Repeat("這是一段測試文字。", 1000));
        var item = new BatchItem(Guid.NewGuid(), 1, "a.txt", Sha(text), 1, BatchPlanner.CountCharacters(text), "a.mp3", BatchItemState.Ready);
        var plan = BatchLongAudioPlanner.Plan(context, batch, item, text);
        var la = Assert.IsType<BatchLongAudioApproval>(plan.Approval);
        Assert.StartsWith($"gs://private-bucket/cloudscribe-batch/{batch.BatchId:N}/", la.OutputGcsUri, StringComparison.Ordinal);
        Assert.EndsWith(la.AttemptId.ToString("N") + ".wav", la.OutputGcsUri, StringComparison.Ordinal);
        Assert.Equal((long)decimal.Ceiling(text.Length * 30m / 10_000m), la.CeilingUsdCents); // the scope's own pricing formula
        Assert.Equal(la.CeilingUsdCents * 2, la.FamilyMaximumUsdCents); // room for one adaptive split of the whole text
        Assert.True(la.AdaptiveRecovery);
        Assert.Null(BatchLongAudioPlanner.Plan(context with { Unsupported = "other voice" }, batch, item, text).Approval);
        Assert.Contains("1 MB", BatchLongAudioPlanner.Plan(context, batch, item, new string('字', 400_000)).Unsupported, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApprovalIsRefusedForFilesNoEngineCanTakeOrOverTheLongAudioCap()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt", "b.txt"]);
        var authority = new MemoryAuthority { Profile = Profile(w.Root, BatchAuthorityMode.InAppWithinLimits) };
        var gate = new BatchAuthorityGate(authority, w.Store, new FixedClock(Now));
        var items = batch.Included.Select(x => new BatchConsentItem(x.ItemId, x.SourceSha256, x.DocumentId!.Value, x.RevisionId!.Value, "plan", 100)).ToArray();
        var request = RequestFor(batch, authority.Profile!, items);
        var withUnnarratable = request with { Items = [.. request.Items, request.Items[0] with { ItemId = Guid.NewGuid(), Engine = null }] };
        Assert.Equal("batch.no-engine", (await gate.ApproveInAppAsync(batch, Account, Project, withUnnarratable, Token)).ReasonCode);
        var la = new BatchLongAudioApproval(Guid.NewGuid(), "gs://b/x.mp3", new string('a', 64), 50, 50, 4096, 24000, 1, false);
        var overCap = request with { Items = [request.Items[0] with { LongAudio = la }, request.Items[1]], LongAudioHeadroomUsdCents = 49 };
        Assert.Equal("batch.long-audio-cap", (await gate.ApproveInAppAsync(batch, Account, Project, overCap, Token)).ReasonCode);
        Assert.Null(authority.Saved);
        Assert.True((await gate.ApproveInAppAsync(batch, Account, Project, request, Token)).Allowed);
    }

    [Fact]
    public async Task FourteenTrackMixedBatchRunsEachFileOnceInOrderWithOneApproval()
    {
        using var w = new Workspace();
        string[] names = ["01 序言.txt", "02 I AM part 1.txt", "03 I AM part 2.txt", "04 I AM part 3.txt", "05 無我 Anatta.txt", "06 Stage 5.txt",
            "07 第六階段 1.txt", "08 第六階段 2.txt", "09 Stage 6 part 3.txt", "10 Stage 6 part 4.txt", "11 Practice.txt", "12 問答.txt", "13 Afterword.txt", "14 Glossary.txt"];
        var (batch, authority, longAudio) = await LongAudioSetupAsync(w, names);
        // Two files go to the ordinary engine instead (as if Long Audio could not take them).
        var ordinaryIds = new[] { batch.Items[5].ItemId, batch.Items[12].ItemId };
        var consent = authority.Consent!;
        var items = consent.Items.Select(x => ordinaryIds.Contains(x.ItemId)
            ? x with { Engine = BatchEngines.CompleteArticle, LongAudio = null, PlanSha256 = "plan-" + x.ItemId.ToString("N"), MaximumMinorUnits = 300 } : x).ToArray();
        authority.Consent = BatchConsent.Create(Guid.NewGuid(), batch, BatchConsentSource.InApp, authority.Profile!, Account, Project, items, Now);
        batch = await w.Store.SaveAsync(batch with { Items = batch.Items.Select(x => ordinaryIds.Contains(x.ItemId) ? x with { Engine = BatchEngines.CompleteArticle } : x).ToArray() }, Token);
        var article = new FakeWorkflow(w.Output);
        foreach (var x in batch.Items.Where(x => ordinaryIds.Contains(x.ItemId))) article.Plans[x.DocumentId!.Value] = "plan-" + x.ItemId.ToString("N");
        var gate = new BatchAuthorityGate(authority, w.Store, new FixedClock(Now));
        var router = new RoutingBatchNarrator(gate, () => (Account, Project), new Dictionary<string, IBatchNarrator>(StringComparer.Ordinal)
        {
            [BatchEngines.LongAudio] = new LongAudioBatchNarrator(gate, longAudio),
            [BatchEngines.CompleteArticle] = new CompleteArticleBatchNarrator(gate, article, () => (Account, Project)),
        });
        var log = new ListLogger();
        var result = await new BatchQueueRunner(w.Store, router, new FixedClock(Now), log).RunAsync(batch.BatchId, batch.SettingsSha256, cancellationToken: Token);
        Assert.Equal(BatchQueueState.Finished, result.State);
        Assert.Equal(14, result.CompletedCount);
        Assert.Equal(12, longAudio.Runs);
        Assert.Equal(2, article.Started);
        Assert.Equal(names.Select(n => Path.ChangeExtension(n, ".mp3")), result.Items.Select(x => x.OutputFileName), StringComparer.Ordinal);
        foreach (var x in result.Items) Assert.True(await BatchOutputFiles.MatchesAsync(x.OutputPath, x.OutputSha256, x.OutputBytes, Token));
        Assert.Equal(14, Directory.GetFiles(w.Output, "*.mp3").Length); // one MP3 per TXT: nothing merged, nothing extra
        Assert.DoesNotContain(log.Messages, m => names.Any(n => m.Contains(BatchPlanner.TitleOf(n), StringComparison.Ordinal)));
    }

    /// <summary>A batch whose files are approved for Long Audio, with a fake Long Audio service.</summary>
    private static async Task<(NarrationBatch Batch, MemoryAuthority Authority, FakeLongAudioWorkflow Workflow)> LongAudioSetupAsync(Workspace w, string[] names)
    {
        var settings = Settings with { Engine = BatchEngines.LongAudioFirst };
        var batch = await w.CreateAsync(names, settings);
        var profile = Profile(w.Root, BatchAuthorityMode.InAppWithinLimits);
        var workflow = new FakeLongAudioWorkflow(w.Root);
        var consentItems = new List<BatchConsentItem>();
        foreach (var item in batch.Items)
        {
            var attempt = Guid.NewGuid();
            var request = FakeLongAudioWorkflow.RequestFor(attempt, $"gs://private-bucket/cloudscribe-batch/{batch.BatchId:N}/{attempt:N}.mp3");
            var la = new BatchLongAudioApproval(attempt, request.OutputGcsUri, request.BindingSha256, 20, 20, 1 << 20, 24000, 1, false);
            workflow.Approvals[item.DocumentId!.Value] = la;
            consentItems.Add(new(item.ItemId, item.SourceSha256, item.DocumentId!.Value, item.RevisionId!.Value, la.RequestBindingSha256, 20, BatchEngines.LongAudio, la));
        }
        batch = await w.Store.SaveAsync(batch with { Items = batch.Items.Select(x => x with { Engine = BatchEngines.LongAudio }).ToArray() }, Token);
        var authority = new MemoryAuthority { Profile = profile, Consent = BatchConsent.Create(Guid.NewGuid(), batch, BatchConsentSource.InApp, profile, Account, Project, consentItems, Now) };
        return (batch, authority, workflow);
    }

    /// <summary>Fake Long Audio service: reviews mirror the approval unless told otherwise; a run saves a verified MP3 once.</summary>
    private sealed class FakeLongAudioWorkflow(string root) : IBatchLongAudioWorkflow
    {
        public Dictionary<Guid, BatchLongAudioApproval> Approvals { get; } = [];
        public HashSet<Guid> Unadmitted { get; } = [];
        public HashSet<Guid> Overpriced { get; } = [];
        public bool NeedsReconciliation { get; set; }
        public LongAudioWorkflowStage StopWith { get; set; } = LongAudioWorkflowStage.Completed;
        public int Runs { get; private set; }
        private readonly Dictionary<Guid, (string, string, long)> _saved = [];

        public Task<GoogleLongAudioDocumentReview> ReviewAsync(Guid documentId, CancellationToken cancellationToken)
        {
            if (Unadmitted.Contains(documentId)) throw new InvalidOperationException("No single configured Long Audio job matches this document. No request was sent.");
            var la = Approvals[documentId];
            var request = RequestFor(la.AttemptId, la.OutputGcsUri);
            var cost = GoogleLongAudioCostReview.Prepare(request, 30m, 0, 0, 100_000, "fixture");
            long estimate = Overpriced.Contains(documentId) ? la.CeilingUsdCents + 1 : 1;
            cost = cost with { EstimatedSynthesisUsdCents = estimate };
            var job = new GoogleLongAudioDocumentJob(documentId, Guid.NewGuid(), new string('a', 64), la.AttemptId, la.OutputGcsUri, "text", 4096, 24000, 1);
            // The binding the service reports is the approval's; a real review recomputes it from the saved text.
            var review = new GoogleLongAudioDocumentReview(documentId, job.RevisionId, "t", job.SourceSha256, false, new string('s', 64), job, request, cost,
                new GoogleLongAudioOutputAllowance(4096, null, "mp3", 24000, 1));
            return Task.FromResult(review);
        }

        /// <summary>The same exact request the real service would compile for this attempt and output (fixed synthetic text).</summary>
        public static GoogleLongAudioRequest RequestFor(Guid attempt, string uri) =>
            GoogleLongAudioRequest.Create(attempt, "123456", "global", "cred", uri, "synthetic text", "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "MP3");

        public Task<GoogleLongAudioContinuationKind> ContinuationAsync(GoogleLongAudioDocumentReview review, CancellationToken cancellationToken) =>
            Task.FromResult(NeedsReconciliation ? GoogleLongAudioContinuationKind.ReconcileRequired : GoogleLongAudioContinuationKind.CorrectLocalSetup);

        public async Task<GoogleLongAudioDocumentRun> RunAsync(GoogleLongAudioDocumentReview review, IProgress<BatchItemProgress>? progress, Guid itemId, CancellationToken cancellationToken)
        {
            Runs++;
            if (StopWith != LongAudioWorkflowStage.Completed) return new(StopWith, new(null!, null));
            string path = Path.Combine(root, review.Request.AttemptId.ToString("N") + ".mp3");
            await File.WriteAllTextAsync(path, "synthetic long audio " + review.DocumentId, cancellationToken);
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            _saved[review.DocumentId] = (path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
            return new(LongAudioWorkflowStage.Completed, new(null!, null));
        }

        public Task<(string Path, string Sha256, long Bytes)?> InspectOutputAsync(GoogleLongAudioDocumentReview review, CancellationToken cancellationToken) =>
            Task.FromResult<(string, string, long)?>(_saved.TryGetValue(review.DocumentId, out var saved) ? saved : null);

        public Task<long?> CommittedUsdCentsAsync(GoogleLongAudioDocumentReview review, CancellationToken cancellationToken) =>
            Task.FromResult<long?>(_saved.ContainsKey(review.DocumentId) || Runs > 0 ? 3 : 0);
    }
}
