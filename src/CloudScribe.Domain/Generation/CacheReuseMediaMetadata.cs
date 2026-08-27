namespace CloudScribe.Domain.Generation;

public sealed record CacheReuseMediaMetadata(
    GenerationAudioFormat Format,
    int SampleRateHz,
    int ChannelCount,
    long DurationMilliseconds)
{
    public CacheReuseMediaMetadata Validate()
    {
        ValidateSampleRate(SampleRateHz);
        ValidateChannelCount(ChannelCount);
        ValidateDuration(DurationMilliseconds);
        return this;
    }

    private static void ValidateSampleRate(int sampleRateHz)
    {
        if (sampleRateHz is < 8000 or > 384000)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
    }

    private static void ValidateChannelCount(int channelCount)
    {
        if (channelCount is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(channelCount));
    }

    private static void ValidateDuration(long durationMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationMilliseconds);
    }
}
