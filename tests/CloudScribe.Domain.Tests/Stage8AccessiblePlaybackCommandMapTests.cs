using CloudScribe.Domain.Accessibility;

namespace CloudScribe.Domain.Tests;

public sealed class Stage8AccessiblePlaybackCommandMapTests
{
    [Fact]
    public void DefaultMap_CoversCoreKeyboardCommands_AndNormalizesKeys()
    {
        var map = AccessiblePlaybackCommandMap.CreateDefault();
        map.EnsureCoreKeyboardCoverage();

        Assert.True(map.TryResolve(new PlaybackKeyGesture(" space "), out var command));
        Assert.Equal(PlaybackAccessibilityCommand.PlayPause, command);
    }

    [Fact]
    public void Constructor_RejectsDuplicateNormalizedGesture()
    {
        var pairs = new[]
        {
            new KeyValuePair<PlaybackKeyGesture, PlaybackAccessibilityCommand>(new("m"), PlaybackAccessibilityCommand.MuteToggle),
            new KeyValuePair<PlaybackKeyGesture, PlaybackAccessibilityCommand>(new(" M "), PlaybackAccessibilityCommand.Stop),
        };
        Assert.Throws<InvalidOperationException>(() => new AccessiblePlaybackCommandMap(pairs));
    }

    [Fact]
    public void EnsureCoreKeyboardCoverage_FailsClosedWhenIncomplete()
    {
        var map = new AccessiblePlaybackCommandMap(new[]
        {
            new KeyValuePair<PlaybackKeyGesture, PlaybackAccessibilityCommand>(new("SPACE"), PlaybackAccessibilityCommand.PlayPause),
        });
        Assert.Throws<InvalidOperationException>(map.EnsureCoreKeyboardCoverage);
    }
}
