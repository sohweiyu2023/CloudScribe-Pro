namespace CloudScribe.Domain.Generation;

public sealed record ProviderRouteFallbackAuthorization(
    ProviderRouteIdentity PinnedRoute,
    ProviderRouteIdentity AllowedFallback,
    string Currency,
    int CurrencyScale,
    long MaximumFallbackMinorUnits)
{
    public ProviderRouteFallbackAuthorization Validate()
    {
        return Validate(PinnedRoute, AllowedFallback, Currency, CurrencyScale, MaximumFallbackMinorUnits);
    }

    private static ProviderRouteFallbackAuthorization Validate(
        ProviderRouteIdentity pinnedRoute,
        ProviderRouteIdentity allowedFallback,
        string currency,
        int currencyScale,
        long maximumFallbackMinorUnits)
    {
        ArgumentNullException.ThrowIfNull(pinnedRoute);
        ArgumentNullException.ThrowIfNull(allowedFallback);
        pinnedRoute.Validate();
        allowedFallback.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        if (currencyScale is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(currencyScale));
        }
        if (maximumFallbackMinorUnits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFallbackMinorUnits));
        }
        if (pinnedRoute == allowedFallback)
        {
            throw new InvalidOperationException("Fallback authorization must identify a distinct route.");
        }
        return new ProviderRouteFallbackAuthorization(pinnedRoute, allowedFallback, currency, currencyScale, maximumFallbackMinorUnits);
    }
}
