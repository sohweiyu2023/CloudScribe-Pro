namespace CloudScribe.Domain.Accessibility;

public sealed class AccessiblePlaybackCommandMap
{
    private readonly IReadOnlyDictionary<PlaybackKeyGesture, PlaybackAccessibilityCommand> _bindings;

    public AccessiblePlaybackCommandMap(IEnumerable<KeyValuePair<PlaybackKeyGesture, PlaybackAccessibilityCommand>> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var normalized = new Dictionary<PlaybackKeyGesture, PlaybackAccessibilityCommand>();
        foreach (var pair in bindings)
        {
            var gesture = pair.Key?.Normalize() ?? throw new ArgumentException("Gesture cannot be null.", nameof(bindings));
            if (!Enum.IsDefined(pair.Value)) throw new ArgumentOutOfRangeException(nameof(bindings));
            if (!normalized.TryAdd(gesture, pair.Value))
                throw new InvalidOperationException($"Duplicate accessible playback key gesture: {gesture.Key}");
        }
        if (normalized.Count == 0) throw new ArgumentException("At least one key binding is required.", nameof(bindings));
        _bindings = normalized;
    }

    public bool TryResolve(PlaybackKeyGesture gesture, out PlaybackAccessibilityCommand command)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        return _bindings.TryGetValue(gesture.Normalize(), out command);
    }

    public void EnsureCoreKeyboardCoverage()
    {
        var commands = _bindings.Values.ToHashSet();
        var required = new[]
        {
            PlaybackAccessibilityCommand.PlayPause,
            PlaybackAccessibilityCommand.SeekBackward,
            PlaybackAccessibilityCommand.SeekForward,
            PlaybackAccessibilityCommand.VolumeDown,
            PlaybackAccessibilityCommand.VolumeUp,
            PlaybackAccessibilityCommand.Stop,
        };
        var missing = required.Where(command => !commands.Contains(command)).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException($"Accessible playback command map is missing core commands: {string.Join(", ", missing)}");
    }

    public static AccessiblePlaybackCommandMap CreateDefault() => new(new Dictionary<PlaybackKeyGesture, PlaybackAccessibilityCommand>
    {
        [new("SPACE")] = PlaybackAccessibilityCommand.PlayPause,
        [new("ARROWLEFT")] = PlaybackAccessibilityCommand.SeekBackward,
        [new("ARROWRIGHT")] = PlaybackAccessibilityCommand.SeekForward,
        [new("ARROWDOWN")] = PlaybackAccessibilityCommand.VolumeDown,
        [new("ARROWUP")] = PlaybackAccessibilityCommand.VolumeUp,
        [new("M")] = PlaybackAccessibilityCommand.MuteToggle,
        [new("[", Control: true)] = PlaybackAccessibilityCommand.SpeedDown,
        [new("]", Control: true)] = PlaybackAccessibilityCommand.SpeedUp,
        [new("N", Control: true)] = PlaybackAccessibilityCommand.NextChapter,
        [new("P", Control: true)] = PlaybackAccessibilityCommand.PreviousChapter,
        [new("B", Control: true)] = PlaybackAccessibilityCommand.Bookmark,
        [new("ESCAPE")] = PlaybackAccessibilityCommand.Stop,
    });
}
