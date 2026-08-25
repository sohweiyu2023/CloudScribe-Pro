namespace CloudScribe.Domain.Generation;

public sealed record TimedTextCue(
    int Sequence,
    TimeSpan Start,
    TimeSpan End,
    string Text,
    string ProvenanceId)
{
    public TimedTextCue Validate()
    {
        return Validate(Sequence, Start, End, Text, ProvenanceId);
    }

    private static TimedTextCue Validate(
        int sequence,
        TimeSpan start,
        TimeSpan end,
        string text,
        string provenanceId)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }
        if (start < TimeSpan.Zero || end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(provenanceId);
        return new TimedTextCue(sequence, start, end, text, provenanceId);
    }
}
