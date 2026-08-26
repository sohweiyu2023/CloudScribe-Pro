namespace CloudScribe.Domain.Accessibility;

public sealed record PlaybackKeyGesture(string Key, bool Control = false, bool Alt = false, bool Shift = false)
{
    public PlaybackKeyGesture Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Key);
        return this with { Key = Key.Trim().ToUpperInvariant() };
    }
}
