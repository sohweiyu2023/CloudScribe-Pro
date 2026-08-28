namespace CloudScribe.Domain.Generation;

public sealed record PlaybackBookmark(string Label, TimeSpan Position)
{
    public PlaybackBookmark Validate(TimeSpan duration)
    {
        ValidateValues(Label, Position, duration);
        return this;
    }

    private static void ValidateValues(string label, TimeSpan position, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (position < TimeSpan.Zero || position > duration)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }
    }
}
