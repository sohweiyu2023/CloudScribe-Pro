namespace CloudScribe.App.Composition;

/// <summary>State of the Long Audio "Stop after current action" and "Stop waiting now" buttons across runs.
/// Every run starts from a clean state, so a stop used in one run never leaves a button disabled in the next.</summary>
public sealed class LongAudioStopButtons
{
    public bool StopEnabled { get; private set; }
    public bool StopNowVisible { get; private set; }
    public bool StopNowEnabled { get; private set; }

    public void RunStarted()
    {
        StopEnabled = true;
        StopNowVisible = false;
        StopNowEnabled = true;
    }

    public void StopRequested(bool runActive)
    {
        StopEnabled = false;
        StopNowVisible = runActive;
        StopNowEnabled = runActive;
    }

    /// <summary>Returns true when the click should cancel the run (only the first click of an active run).</summary>
    public bool StopNowClicked()
    {
        if (!StopNowVisible || !StopNowEnabled) return false;
        StopNowEnabled = false;
        return true;
    }

    public void RunEnded()
    {
        StopEnabled = false;
        StopNowVisible = false;
        StopNowEnabled = true;
    }
}
