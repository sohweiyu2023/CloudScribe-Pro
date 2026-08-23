namespace CloudScribe.Domain.Generation;

public sealed record CacheReuseMediaMetadata(
    GenerationAudioFormat Format,
    int SampleRateHz,
    int ChannelCount,
    long DurationMilliseconds)
{
    public CacheReuseMediaMetadata Validate()
    {
        if (SampleRateHz is < 8000 or > 384000)
            throw new ArgumentOutOfRangeException(nameof(SampleRateHz));
        if (ChannelCount is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(ChannelCount));
        if (DurationMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(DurationMilliseconds));
        return this;
    }
}

public static class CacheReuseMediaPolicy
{
    public static bool IsEligible(
        CacheReuseMediaMetadata cached,
        CacheReuseMediaMetadata expected,
        long durationToleranceMilliseconds = 20)
    {
        ArgumentNullException.ThrowIfNull(cached);
        ArgumentNullException.ThrowIfNull(expected);
        cached.Validate();
        expected.Validate();
        if (durationToleranceMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(durationToleranceMilliseconds));

        return cached.Format == expected.Format &&
            cached.SampleRateHz == expected.SampleRateHz &&
            cached.ChannelCount == expected.ChannelCount &&
            Math.Abs(cached.DurationMilliseconds - expected.DurationMilliseconds) <= durationToleranceMilliseconds;
    }
}
