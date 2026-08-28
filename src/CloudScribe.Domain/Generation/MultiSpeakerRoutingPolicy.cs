namespace CloudScribe.Domain.Generation;

public static class MultiSpeakerRoutingPolicy
{
    public static SpeakerRoute Select(
        string speakerId,
        IReadOnlyCollection<SpeakerRoute> routes,
        bool providerHealthy,
        bool explicitFallbackAllowed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        ArgumentNullException.ThrowIfNull(routes);

        var primary = routes.SingleOrDefault(route =>
            !route.IsFallback && string.Equals(route.SpeakerId, speakerId, StringComparison.Ordinal));
        if (primary is null) throw new InvalidOperationException("No explicit primary route exists for the speaker turn.");
        if (providerHealthy) return primary;
        if (!explicitFallbackAllowed) throw new InvalidOperationException("Provider outage requires explicit fallback authorization.");

        var fallback = routes.SingleOrDefault(route =>
            route.IsFallback && string.Equals(route.SpeakerId, speakerId, StringComparison.Ordinal));
        return fallback ?? throw new InvalidOperationException("No authorized fallback route exists for the speaker turn.");
    }
}
