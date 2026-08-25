namespace CloudScribe.Domain.Generation;

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
