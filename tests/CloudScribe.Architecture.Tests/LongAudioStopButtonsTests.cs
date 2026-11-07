using CloudScribe.App.Composition;

namespace CloudScribe.Architecture.Tests;

public sealed class LongAudioStopButtonsTests
{
    [Fact]
    public void StopWaitingNowIsUsableAgainOnTheNextRun()
    {
        var state = new LongAudioStopButtons();

        state.RunStarted();
        Assert.True(state.StopEnabled);
        Assert.False(state.StopNowVisible);

        state.StopRequested(runActive: true);
        Assert.False(state.StopEnabled);
        Assert.True(state.StopNowVisible);
        Assert.True(state.StopNowEnabled);

        Assert.True(state.StopNowClicked());
        Assert.False(state.StopNowEnabled);
        Assert.False(state.StopNowClicked()); // A second click cannot cancel twice.

        state.RunEnded();
        Assert.False(state.StopNowVisible);

        // Regression: the second run must offer a working Stop waiting now again.
        state.RunStarted();
        Assert.True(state.StopEnabled);
        state.StopRequested(runActive: true);
        Assert.True(state.StopNowVisible);
        Assert.True(state.StopNowEnabled);
        Assert.True(state.StopNowClicked());
    }

    [Fact]
    public void StopWaitingNowStaysHiddenWithoutAnActiveRun()
    {
        var state = new LongAudioStopButtons();
        state.StopRequested(runActive: false);
        Assert.False(state.StopNowVisible);
        Assert.False(state.StopNowClicked());
        state.RunEnded();
        Assert.False(state.StopEnabled);
        Assert.False(state.StopNowVisible);
    }
}
