namespace CloudScribe.Domain.Generation;

public sealed class TimedTextTrack
{
    public TimedTextTrack(IEnumerable<TimedTextCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        Cues = cues.Select(static cue => cue.Validate()).OrderBy(static cue => cue.Sequence).ToArray();
        if (Cues.Select(static cue => cue.Sequence).Distinct().Count() != Cues.Count)
        {
            throw new ArgumentException("Timed-text cue sequence numbers must be unique.", nameof(cues));
        }
        for (var index = 1; index < Cues.Count; index++)
        {
            if (Cues[index].Start < Cues[index - 1].End)
            {
                throw new ArgumentException("Timed-text cues must not overlap.", nameof(cues));
            }
        }
    }

    public IReadOnlyList<TimedTextCue> Cues { get; }
}
