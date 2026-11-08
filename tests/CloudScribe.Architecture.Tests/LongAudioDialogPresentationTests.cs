using CloudScribe.App.Composition;
using CloudScribe.Application.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed class LongAudioDialogPresentationTests
{
    [Fact]
    public void EveryStageHasAPlainHeadlineAndAValidStep()
    {
        foreach (var stage in Enum.GetValues<LongAudioWorkflowStage>())
        {
            Assert.False(string.IsNullOrWhiteSpace(LongAudioDialogPresentation.Headline(stage)));
            Assert.InRange(LongAudioDialogPresentation.StepIndex(stage), -1, LongAudioDialogPresentation.Steps.Count - 1);
        }
        Assert.Equal(-1, LongAudioDialogPresentation.StepIndex(LongAudioWorkflowStage.Failed));
        Assert.Equal(-1, LongAudioDialogPresentation.StepIndex(LongAudioWorkflowStage.DeferredGap));
        Assert.Equal(LongAudioDialogPresentation.Steps.Count - 1, LongAudioDialogPresentation.StepIndex(LongAudioWorkflowStage.Completed));
        Assert.Equal("Your MP3 is ready", LongAudioDialogPresentation.Headline(LongAudioWorkflowStage.Completed));
    }

    [Theory]
    [InlineData(0, 2, "done")]
    [InlineData(2, 2, "current")]
    [InlineData(3, 2, "not started")]
    [InlineData(0, -1, "not active")]
    public void StepStatesAreAnnouncedInWords(int step, int current, string expected) =>
        Assert.Equal(expected, LongAudioDialogPresentation.StepState(step, current));

    [Theory]
    [InlineData(0L, "0 bytes")]
    [InlineData(512L, "512 bytes")]
    [InlineData(2048L, "2.0 KB (2,048 bytes)")]
    [InlineData(40003004L, "38.1 MB (40,003,004 bytes)")]
    public void SizesAreReadableAndKeepExactBytes(long bytes, string expected) =>
        Assert.Equal(expected, LongAudioDialogPresentation.ReadableSize(bytes));

    [Fact]
    public void StoppedJobDoesNotPromiseItCanResume()
    {
        Assert.Equal("Stopped: saved job preserved", LongAudioDialogPresentation.Headline(LongAudioWorkflowStage.Stopped));
        Assert.Contains("only when safe", LongAudioDialogPresentation.StoppedGuidance, StringComparison.Ordinal);
        Assert.Contains("uncertain request is not resent", LongAudioDialogPresentation.StoppedGuidance, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifiedOutputDoesNotResolveAnEarlierUnknownRequest()
    {
        string text = LongAudioDialogPresentation.CompletedRequestNotice(true);
        Assert.Contains("outcome remains unknown", text, StringComparison.Ordinal);
        Assert.Contains("safety hold stays reserved", text, StringComparison.Ordinal);
        Assert.Contains("MP3 is verified; nothing needs to be generated again", text, StringComparison.Ordinal);
        Assert.Contains("not a confirmed Google charge", text, StringComparison.Ordinal);
        Assert.Empty(LongAudioDialogPresentation.CompletedRequestNotice(false));
    }
}
