using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public static class GoogleCacheReuseEligibility
{
    public static bool IsEligible(
        GenerationCacheTrustContext cached,
        GenerationCacheTrustContext current)
    {
        ArgumentNullException.ThrowIfNull(cached);
        ArgumentNullException.ThrowIfNull(current);
        cached.Validate();
        current.Validate();

        return cached.Values().SequenceEqual(current.Values(), StringComparer.Ordinal);
    }

    public static void RequireEligible(
        GenerationCacheTrustContext cached,
        GenerationCacheTrustContext current)
    {
        if (!IsEligible(cached, current))
        {
            throw new InvalidOperationException(
                "Google cache reuse is forbidden because the current provider/account/project/model/voice/policy trust context differs from the cached context.");
        }
    }
}
