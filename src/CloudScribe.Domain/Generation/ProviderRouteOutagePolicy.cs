namespace CloudScribe.Domain.Generation;

public static class ProviderRouteOutagePolicy
{
    public static ProviderRouteIdentity Resolve(
        ProviderRouteIdentity pinnedRoute,
        bool pinnedRouteAvailable,
        ProviderRouteIdentity? proposedFallback,
        ProviderRouteFallbackAuthorization? fallbackAuthorization,
        string billedCurrency,
        int billedCurrencyScale,
        long projectedFallbackMinorUnits)
    {
        pinnedRoute.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(billedCurrency);
        ArgumentOutOfRangeException.ThrowIfNegative(billedCurrencyScale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(billedCurrencyScale, 9);
        ArgumentOutOfRangeException.ThrowIfNegative(projectedFallbackMinorUnits);

        if (pinnedRouteAvailable)
            return pinnedRoute;

        if (proposedFallback is null || fallbackAuthorization is null)
            throw new InvalidOperationException("Pinned provider route is unavailable and no explicit fallback authorization exists.");

        proposedFallback.Validate();
        fallbackAuthorization.Validate();

        if (fallbackAuthorization.PinnedRoute != pinnedRoute || fallbackAuthorization.AllowedFallback != proposedFallback)
            throw new InvalidOperationException("Fallback route is not the route explicitly authorized for this pinned route.");
        if (!string.Equals(fallbackAuthorization.Currency, billedCurrency, StringComparison.OrdinalIgnoreCase) ||
            fallbackAuthorization.CurrencyScale != billedCurrencyScale)
            throw new InvalidOperationException("Fallback billing currency or scale changed after authorization.");
        if (projectedFallbackMinorUnits > fallbackAuthorization.MaximumFallbackMinorUnits)
            throw new InvalidOperationException("Fallback projected cost exceeds the explicit authorization ceiling.");

        return proposedFallback;
    }
}
