namespace CloudScribe.Infrastructure.Generation;

/// <summary>Trusted storage-read allowance, not synthesis authority. No values are inferred from a proposal.</summary>
public sealed record GoogleLongAudioOutputAllowance(long MaximumBytes, DateTimeOffset? ReadNotAfterUtc,
    string ExpectedCodec, int SampleRate, int Channels);
