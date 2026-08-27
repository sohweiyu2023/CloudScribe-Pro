namespace CloudScribe.Domain.Accessibility;

public sealed record PlaybackKeyGesture(string Key, bool Control = false, bool Alt = false, bool Shift = false)
{
    public PlaybackKeyGesture Normalize()
    {
        var key = NormalizeKey(Key);
        return this with { Key = key };
    }

    private static string NormalizeKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return key.Trim().ToUpperInvariant();
    }
}
