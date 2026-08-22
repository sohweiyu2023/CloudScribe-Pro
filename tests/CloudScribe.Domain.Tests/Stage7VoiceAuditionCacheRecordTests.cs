using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage7VoiceAuditionCacheRecordTests
{
    [Fact]
    public void CacheReuseRequiresExactAuthorizedRoutePricingSampleAndFormat()
    {
        var authorization = Authorization("pricing:v1", "hello world");
        var record = VoiceAuditionCacheRecord.Create(
            authorization,
            "wav",
            new string('a', 64),
            TimeSpan.FromSeconds(4));

        Assert.True(record.CanReuse(authorization, "WAV"));
        Assert.False(record.CanReuse(Authorization("pricing:v2", "hello world"), "wav"));
        Assert.False(record.CanReuse(Authorization("pricing:v1", "different sample"), "wav"));
        Assert.False(record.CanReuse(authorization, "mp3"));
    }

    [Fact]
    public void InvalidMediaIdentityAndUnboundedDurationFailClosed()
    {
        var authorization = Authorization("pricing:v1", "hello world");

        Assert.Throws<ArgumentException>(() => VoiceAuditionCacheRecord.Create(
            authorization,
            "wav",
            "not-a-sha",
            TimeSpan.FromSeconds(4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => VoiceAuditionCacheRecord.Create(
            authorization,
            "wav",
            new string('b', 64),
            TimeSpan.FromMinutes(6)));
    }

    private static VoiceAuditionAuthorization Authorization(string pricing, string sample) =>
        VoiceAuditionAuthorization.Create(
            "provider:stable",
            "account:1",
            "voice:1",
            pricing,
            "USD",
            100,
            6,
            sample);
}
