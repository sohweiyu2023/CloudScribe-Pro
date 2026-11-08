using CloudScribe.App.Composition;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;

namespace CloudScribe.Architecture.Tests;

/// <summary>The batch window's step guide, disabled-button explanations, rehearsal verdict and result text.</summary>
public sealed class BatchGuideTests
{
    private static BatchGuide.Input Fresh => new(0, 0, false, "Select a verified Google voice first.", true, false, false, false, false,
        null, false, 0, false, false, false, false, 0, 0, 0, 0, false);

    [Fact]
    public void FirstUseWalksFilesThenVoiceAndOutputAndNeverImpliesAPaidBatchIsReady()
    {
        var empty = BatchGuide.For(Fresh);
        Assert.StartsWith("Next: Add TXT files", empty.Next, StringComparison.Ordinal);
        Assert.Equal(BatchGuide.StepStatus.Current, empty.Steps[0].Status);
        Assert.Contains(empty.SaveBlockers, x => x.Contains("voice", StringComparison.Ordinal));
        Assert.Contains(empty.SaveBlockers, x => x.Contains("output folder", StringComparison.Ordinal));

        var files = BatchGuide.For(Fresh with { Files = 3, Usable = 3 });
        Assert.Equal("Next: Select a verified Google voice first.", files.Next);
        Assert.Equal(BatchGuide.StepStatus.Done, files.Steps[0].Status);

        var ready = BatchGuide.For(Fresh with { Files = 3, Usable = 3, HasVoice = true, VoiceProblem = null, HasOutputFolder = true });
        Assert.Empty(ready.SaveBlockers);
        Assert.Contains("Add to library and save batch", ready.Next, StringComparison.Ordinal);

        // Saved, but this computer has no batch spending setup (the installed profile today): say so plainly.
        var noProfile = BatchGuide.For(Fresh with { Files = 3, Usable = 3, HasVoice = true, VoiceProblem = null, HasOutputFolder = true, Saved = true, Remaining = 3 });
        Assert.Contains(noProfile.StartBlockers, x => x.Contains("not set up on this computer", StringComparison.Ordinal));
        Assert.Contains("no paid batch can start", noProfile.Next, StringComparison.Ordinal);
        Assert.Equal(BatchGuide.StepStatus.Blocked, noProfile.Steps[2].Status);
        Assert.DoesNotContain(noProfile.Steps, x => string.Equals(x.Name, "Start", StringComparison.Ordinal) && x.Status == BatchGuide.StepStatus.Current);
    }

    [Fact]
    public void EveryStartBlockerIsListedAndApprovalUnlocksStart()
    {
        var saved = Fresh with
        {
            Files = 2,
            Usable = 2,
            HasVoice = true,
            VoiceProblem = null,
            HasOutputFolder = true,
            Saved = true,
            ProfileInstalled = true,
            Mode = BatchAuthorityMode.InAppWithinLimits,
            Reviewed = true,
            Remaining = 2,
        };
        var needsApproval = BatchGuide.For(saved with { Unnarratable = 1, NeedsCaptureConsent = true });
        Assert.Contains(needsApproval.StartBlockers, x => x.Contains("Remove the 1 file", StringComparison.Ordinal));
        Assert.Contains(needsApproval.StartBlockers, x => x.Contains("Approve the batch", StringComparison.Ordinal));
        Assert.Contains(needsApproval.StartBlockers, x => x.Contains("Long Audio private-response box", StringComparison.Ordinal));
        string tip = BatchGuide.Explain("normal", needsApproval.StartBlockers);
        Assert.StartsWith("Not available yet:", tip, StringComparison.Ordinal);
        Assert.Equal(3, tip.Split('•').Length - 1);

        var approved = BatchGuide.For(saved with { Approved = true });
        Assert.Empty(approved.StartBlockers);
        Assert.Equal("Next: choose Start. Files are narrated one at a time, in the order shown.", approved.Next);
        Assert.Equal("normal", BatchGuide.Explain("normal", approved.StartBlockers));

        var wrongVoice = BatchGuide.For(saved with { Approved = true, VoiceMatchesBatch = false });
        Assert.Contains(wrongVoice.StartBlockers, x => x.Contains("Use this batch's voice", StringComparison.Ordinal));
        var pending = BatchGuide.For(saved with { Approved = true, PendingImport = 2 });
        Assert.Contains(pending.StartBlockers, x => x.Contains("Finish adding 2 file(s)", StringComparison.Ordinal));
        var busy = BatchGuide.For(saved with { Approved = true, Busy = true });
        Assert.Contains("Wait for the current step to finish.", busy.StartBlockers, StringComparer.Ordinal);
        var reviewed = BatchGuide.For(saved with { Mode = BatchAuthorityMode.Reviewed });
        Assert.Contains(reviewed.StartBlockers, x => x.Contains("Export the batch request", StringComparison.Ordinal));
    }

    [Fact]
    public void ResultsAreSeparatedAndRunningHidesSaveAndStartAdvice()
    {
        var finished = BatchGuide.For(Fresh with { Files = 3, Usable = 3, HasVoice = true, HasOutputFolder = true, Saved = true, Finished = true, Completed = 1, Failed = 1, Uncertain = 1 });
        Assert.Contains("1 failed and 1 needing checking", finished.Next, StringComparison.Ordinal);
        Assert.Equal(BatchGuide.StepStatus.Blocked, finished.Steps[4].Status);
        var running = BatchGuide.For(Fresh with { Files = 3, Usable = 3, Saved = true, Running = true, Remaining = 2 });
        Assert.Empty(running.SaveBlockers);
        Assert.Equal(["The batch is running."], running.StartBlockers);
        Assert.StartsWith("Running.", running.Next, StringComparison.Ordinal);
        Assert.Contains("▶ 4 Start", running.StepLine, StringComparison.Ordinal);
    }

    [Fact]
    public void RehearsalVerdictCountsBatchChecksNotOnlyFiles()
    {
        var report = new BatchRehearsalReport();
        report.Block("Select the batch voice.");
        report.Block("The output folder is missing.");
        report.Pass("tools");
        report.File(true, "File 01 ok");
        report.File(true, "File 02 ok");
        Assert.False(report.Ready);
        Assert.Equal("Not ready: 2 batch check(s) failed (2 file(s) ready). Fix the ✗ lines first. Nothing was sent.", report.Verdict);
        Assert.DoesNotContain("All 2", report.ToString(), StringComparison.Ordinal);

        var mixed = new BatchRehearsalReport();
        mixed.File(false, "File 01 not approved");
        mixed.File(true, "File 02 ok");
        mixed.Note("Audio tools will be asked for.");
        Assert.Equal("Not ready: 1 file(s) would stop (1 file(s) ready). Fix the ✗ lines first. Nothing was sent.", mixed.Verdict);

        var ready = new BatchRehearsalReport();
        ready.Pass("voice"); ready.Note("tools later"); ready.File(true, "File 01 ok");
        Assert.True(ready.Ready);
        Assert.Equal("Ready: all 1 remaining file(s) can start. Nothing was sent.", ready.Verdict);
        Assert.EndsWith(ready.Verdict, ready.ToString(), StringComparison.Ordinal);
    }

    private static readonly BatchSettings Settings = new("cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "Alnilam (cmn-CN)", BatchEngines.CompleteArticle, BatchFailurePolicy.SkipAndContinue);

    private static BatchAuthorityProfile Profile => new(2, BatchAuthorityMode.Reviewed, new string('a', 64), "acct", "proj", [], [BatchEngines.CompleteArticle],
        BatchFailurePolicy.SkipAndContinue, "USD", 2, 1924, 7693, 30, 500, TimeSpan.FromDays(2), DateTimeOffset.MaxValue, "SGD", 1.30m, 3000);

    private static BatchItem Item(int order, BatchItemState state, int uncertain = 0, long reserved = 0) =>
        new(Guid.NewGuid(), order, $"{order:00} Part.txt", "sha" + order, 100, 10_000, $"{order:00} Part.mp3", state, ReservedMinorUnits: reserved, UncertainOutcomes: uncertain);

    [Fact]
    public void MoneyIsShownAsEstimateAllowanceAndPossibleChargesNeverAsABill()
    {
        var batch = new NarrationBatch(1, Guid.NewGuid(), "b", 1, BatchQueueState.Paused, Settings, Settings.Sha256(), "C:\\out", "USD", 2,
            [Item(1, BatchItemState.Completed, uncertain: 1, reserved: 33), Item(2, BatchItemState.Failed), Item(3, BatchItemState.Skipped), Item(4, BatchItemState.Ready)],
            DateTimeOffset.UnixEpoch);
        string text = BatchDialogPresentation.Summary(batch, Profile, approvedMinorUnits: 200);
        Assert.Contains("1 of 3 done · 1 failed · 1 skipped", text, StringComparison.Ordinal);
        Assert.Contains("Estimate (upper limit, not a bill)", text, StringComparison.Ordinal);
        Assert.Contains("Approved allowance: up to about S$2.60 (US$2.00)", text, StringComparison.Ordinal);
        Assert.Contains("1 file(s) had a request that may have reached Google", text, StringComparison.Ordinal);
        Assert.Contains("Your Google Cloud bill is the only record of actual charges.", text, StringComparison.Ordinal);
        Assert.Contains("Approved allowance: none yet.", BatchDialogPresentation.Summary(batch, Profile), StringComparison.Ordinal);
        Assert.Contains("an earlier request for this file may have reached Google", BatchDialogPresentation.Detail(batch.Items[0], Profile), StringComparison.Ordinal);

        string results = BatchDialogPresentation.Results(batch);
        Assert.Contains("1 MP3 done · 1 failed · 0 need checking · 1 skipped by you · 1 waiting", results, StringComparison.Ordinal);
        Assert.Contains("retried without new speech", results, StringComparison.Ordinal);
    }

    [Fact]
    public void SameTextNoteNamesTheEarlierFileAndSourceProblemsAreExplained()
    {
        var item = Item(3, BatchItemState.Ready) with { SameTextAs = 1 };
        Assert.Contains("same text as file 01 (01 Part.txt), kept as its own track", BatchDialogPresentation.Detail(item, null, "01 Part.txt"), StringComparison.Ordinal);
        Assert.Contains("TXT file changed after it was listed", BatchDialogPresentation.Reason("batch.source-changed-add-again"), StringComparison.Ordinal);
        Assert.Contains("moved, renamed or deleted", BatchDialogPresentation.Reason("batch.source-missing-add-again"), StringComparison.Ordinal);
        Assert.Contains("Drag rows", BatchDialogPresentation.DraftSummary([item]), StringComparison.Ordinal);
        Assert.Contains("not set up on this computer", BatchDialogPresentation.Authority(null), StringComparison.Ordinal);
    }
}
