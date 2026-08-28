using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage7VoiceAuditionAuthorizationTests
{
    [Fact]
    public void ExactAuditionWithinCeilingIsAuthorized()
    {
        var authorization = VoiceAuditionAuthorization.Create("provider-a", "account-a", "voice-a", "pricing-v1", "usd", 250, 4, "Hello world");

        authorization.EnsureAuthorized("provider-a", "account-a", "voice-a", "pricing-v1", "USD", 200, 4, "Hello world");

        Assert.Contains("provider-a:account-a:voice-a:pricing-v1:", authorization.CacheIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public void RoutePricingSampleOrSpendDriftFailsClosed()
    {
        var authorization = VoiceAuditionAuthorization.Create("provider-a", "account-a", "voice-a", "pricing-v1", "USD", 250, 4, "Hello world");

        Assert.Throws<InvalidOperationException>(() => authorization.EnsureAuthorized("provider-b", "account-a", "voice-a", "pricing-v1", "USD", 200, 4, "Hello world"));
        Assert.Throws<InvalidOperationException>(() => authorization.EnsureAuthorized("provider-a", "account-a", "voice-a", "pricing-v2", "USD", 200, 4, "Hello world"));
        Assert.Throws<InvalidOperationException>(() => authorization.EnsureAuthorized("provider-a", "account-a", "voice-a", "pricing-v1", "USD", 200, 4, "Changed text"));
        Assert.Throws<InvalidOperationException>(() => authorization.EnsureAuthorized("provider-a", "account-a", "voice-a", "pricing-v1", "USD", 251, 4, "Hello world"));
    }
}
