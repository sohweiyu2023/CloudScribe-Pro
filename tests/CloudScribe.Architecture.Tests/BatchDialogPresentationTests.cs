using CloudScribe.App.Composition;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;

namespace CloudScribe.Architecture.Tests;

public sealed class BatchDialogPresentationTests
{
    private static readonly BatchSettings Settings = new("cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "Alnilam (cmn-CN)", BatchEngines.CompleteArticle, BatchFailurePolicy.SkipAndContinue);

    private static BatchAuthorityProfile Profile => new(2, BatchAuthorityMode.Reviewed, new string('a', 64), "acct", "proj", [], [BatchEngines.CompleteArticle],
        BatchFailurePolicy.SkipAndContinue, "USD", 2, 1924, 7693, 30, 500, TimeSpan.FromDays(2), DateTimeOffset.MaxValue, "SGD", 1.30m, 3000);

    private static BatchItem Item(int order, BatchItemState state, int characters = 10_000, long active = 0, string? reason = null) =>
        new(Guid.NewGuid(), order, $"{order:00} Part.txt", "sha", 100, characters, $"{order:00} Part.mp3", state, BatchExclusion.None,
            Guid.NewGuid(), Guid.NewGuid(), null, reason, 0, 0, 0, null, 0, null, null, null, active);

    private static NarrationBatch Batch(params BatchItem[] items) =>
        new(1, Guid.NewGuid(), "b", 1, BatchQueueState.Paused, Settings, Settings.Sha256(), "C:\\out", "USD", 2, items, DateTimeOffset.UnixEpoch, ActiveMilliseconds: 0);

    [Fact]
    public void RowsShowOrderOutputCostAndPlainState()
    {
        string row = BatchDialogPresentation.Row(Item(3, BatchItemState.Uncertain, reason: "article.provider-outcome-uncertain-no-retry"), Profile);
        Assert.Contains("03  03 Part.txt  →  03 Part.mp3", row, StringComparison.Ordinal);
        Assert.Contains("about S$0.43 (US$0.33)", row, StringComparison.Ordinal);
        Assert.Contains("needs checking — Google's answer was not received; it will not be resent automatically", row, StringComparison.Ordinal);
        var excluded = Item(4, BatchItemState.Excluded) with { Exclusion = BatchExclusion.NotUnicodeText };
        Assert.Contains("save it as UTF-8", BatchDialogPresentation.Row(excluded, Profile), StringComparison.Ordinal);
        Assert.DoesNotContain("est.", BatchDialogPresentation.Row(Item(1, BatchItemState.Ready), null), StringComparison.Ordinal);
    }

    [Fact]
    public void TonesAndQueueStatesAreReadable()
    {
        Assert.Equal("done", BatchDialogPresentation.Tone(Item(1, BatchItemState.Completed)));
        Assert.Equal("attention", BatchDialogPresentation.Tone(Item(1, BatchItemState.Uncertain)));
        Assert.Equal("failed", BatchDialogPresentation.Tone(Item(1, BatchItemState.Failed)));
        Assert.Equal("waiting", BatchDialogPresentation.Tone(Item(1, BatchItemState.Ready)));
        Assert.Equal("muted", BatchDialogPresentation.Tone(Item(1, BatchItemState.Skipped)));
        Assert.Equal("paused", BatchDialogPresentation.QueueState(BatchQueueState.Paused));
        Assert.Equal("not started", BatchDialogPresentation.QueueState(BatchQueueState.Ready));
    }

    [Fact]
    public void SummaryLabelsEstimatesAndHoldsAsNotABill()
    {
        var text = BatchDialogPresentation.Summary(Batch(Item(1, BatchItemState.Completed), Item(2, BatchItemState.Failed), Item(3, BatchItemState.Ready)), Profile);
        Assert.Contains("1 of 3 done · 1 failed", text, StringComparison.Ordinal);
        Assert.Contains("not your Google bill", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TimingOnlyGivesARoughRemainingTimeAfterTwoFinishedFiles()
    {
        var one = Batch(Item(1, BatchItemState.Completed, 1000, 60_000), Item(2, BatchItemState.Ready, 2000)) with { ActiveMilliseconds = 60_000 };
        Assert.DoesNotContain("left", BatchDialogPresentation.Timing(one, TimeSpan.Zero), StringComparison.Ordinal);
        var two = Batch(Item(1, BatchItemState.Completed, 1000, 60_000), Item(2, BatchItemState.Completed, 1000, 60_000), Item(3, BatchItemState.Ready, 3000))
            with
        { ActiveMilliseconds = 120_000 };
        Assert.Equal("Active time 2 min 0 s · roughly 3 min 0 s left (based on finished files; provider waits vary).", BatchDialogPresentation.Timing(two, TimeSpan.Zero));
        Assert.Equal("Not started yet.", BatchDialogPresentation.Timing(Batch(Item(1, BatchItemState.Ready)), TimeSpan.Zero));
    }

    [Fact]
    public void OverallProgressCountsFinishedFilesAndTheCurrentFilesSavedParts()
    {
        var batch = Batch(Item(1, BatchItemState.Completed), Item(2, BatchItemState.Running), Item(3, BatchItemState.Ready), Item(4, BatchItemState.Ready));
        Assert.Equal(25, BatchDialogPresentation.OverallPercentage(batch, null));
        Assert.Equal(37.5, BatchDialogPresentation.OverallPercentage(batch, new(batch.Items[1].ItemId, "x", 1, 2)));
        Assert.Equal(0, BatchDialogPresentation.OverallPercentage(Batch(), null));
    }

    [Fact]
    public void AuthorityTextExplainsEachMode()
    {
        Assert.Contains("not set up", BatchDialogPresentation.Authority(null), StringComparison.Ordinal);
        Assert.Contains("export the batch request", BatchDialogPresentation.Authority(Profile), StringComparison.Ordinal);
        Assert.Contains("about S$25.01 (US$19.24) per batch", BatchDialogPresentation.Authority(Profile), StringComparison.Ordinal);
        Assert.Contains("approve batches here", BatchDialogPresentation.Authority(Profile with { Mode = BatchAuthorityMode.InAppWithinLimits }), StringComparison.Ordinal);
        Assert.Contains("turned off", BatchDialogPresentation.Authority(Profile with { Mode = BatchAuthorityMode.Off }), StringComparison.Ordinal);
    }

    [Fact]
    public void RowsShowEngineAndKeepDuplicateTextAsANote()
    {
        var item = Item(4, BatchItemState.Ready) with { Engine = BatchEngines.LongAudio, SameTextAs = 2, EstimatedMaximumMinorUnits = 120 };
        string detail = BatchDialogPresentation.Detail(item, Profile);
        Assert.Contains("Long Audio", detail, StringComparison.Ordinal);
        Assert.Contains("up to about S$1.56 (US$1.20)", detail, StringComparison.Ordinal);
        Assert.Contains("same text as file 02 (kept)", detail, StringComparison.Ordinal);
        Assert.Contains("Retry copies it again with no new request", BatchDialogPresentation.Reason("batch.local-copy-failed-speech-kept"), StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewSummaryExplainsCapRoomAndFilesThatWillNotRun()
    {
        string fits = BatchDialogPresentation.ReviewSummary(12, 2, 1, 0, 4000, 3000, 5000, Profile);
        Assert.Contains("12 file(s) by Long Audio, 2 by the ordinary engine", fits, StringComparison.Ordinal);
        Assert.Contains("room left in the installed Long Audio cap", fits, StringComparison.Ordinal);
        Assert.Contains("1 ordinary-engine file(s) still need an installed article admission", fits, StringComparison.Ordinal);
        Assert.Contains("not a bill", fits, StringComparison.Ordinal);
        string over = BatchDialogPresentation.ReviewSummary(3, 0, 0, 1, 4000, 6000, 5000, Profile);
        Assert.Contains("Raise the cap with the reviewed installer", over, StringComparison.Ordinal);
        Assert.Contains("1 file(s) cannot be narrated", over, StringComparison.Ordinal);
        Assert.Equal(BatchEngines.LongAudioFirst, BatchDialogPresentation.EngineForChoice(0));
        Assert.Equal(BatchEngines.CompleteArticle, BatchDialogPresentation.EngineForChoice(1));
    }

    [Theory]
    [InlineData("batch.long-audio-stopped-check-again")]
    [InlineData("batch.long-audio-failed-check-again")]
    [InlineData("batch.long-audio-output-not-verified")]
    public void CheckAgainDefersTheSavedJobCheckUntilResume(string reason)
    {
        Assert.Contains("when you resume", BatchDialogPresentation.Reason(reason), StringComparison.Ordinal);
        Assert.Contains("when you resume the batch", BatchDialogPresentation.CheckAgainGuidance, StringComparison.Ordinal);
        Assert.Contains("never repeats an uncertain request", BatchDialogPresentation.CheckAgainGuidance, StringComparison.Ordinal);
        Assert.Contains("cannot check that request with Google", BatchDialogPresentation.CheckAgainGuidance, StringComparison.Ordinal);
        Assert.Contains("Without a saved Google operation ID", BatchDialogPresentation.Reason("batch.long-audio-needs-reconciliation"), StringComparison.Ordinal);
    }

    [Fact]
    public void UnconfiguredAuthorityDoesNotPromiseOfflineSaveOrRehearsal()
    {
        string text = BatchDialogPresentation.Authority(null);
        Assert.Contains("prepare files now", text, StringComparison.Ordinal);
        Assert.Contains("saving or rehearsing also requires a verified voice", text, StringComparison.Ordinal);
    }

}
