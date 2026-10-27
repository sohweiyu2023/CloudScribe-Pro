using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class HistoricalAudioImportReviewStateTests
{
    [Fact]
    public void ConfirmationCannotCreateAuthorityOrSurviveSelectionChangesAndReplay()
    {
        var state = new HistoricalAudioImportReviewState(); state.ConfirmExactReview(true); Assert.False(state.CanImport);
        state.SelectEvidence("admitted-A"); int version = state.SelectionVersion;
        var preview = new ArticleHistoricalImportPreview(Guid.NewGuid(), new string('a', 64), Guid.NewGuid(), 2, "synthetic-voice", "en-US", 128);
        Assert.True(state.PresentPreview(version, preview)); Assert.False(state.CanImport);
        state.ConfirmExactReview(true); Assert.True(state.CanImport);
        state.SelectEvidence("admitted-B"); Assert.False(state.CanImport);
        Assert.False(state.PresentPreview(version, preview)); Assert.Throws<InvalidOperationException>(() => state.TakeConfirmedPreview());
        Assert.True(state.PresentPreview(state.SelectionVersion, preview)); state.ConfirmExactReview(true);
        Assert.Equal(preview, state.TakeConfirmedPreview()); Assert.False(state.CanImport);
        Assert.Throws<InvalidOperationException>(() => state.TakeConfirmedPreview());
    }
}
