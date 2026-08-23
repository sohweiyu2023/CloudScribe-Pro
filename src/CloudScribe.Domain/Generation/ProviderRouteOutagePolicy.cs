namespace CloudScribe.Domain.Generation;

public sealed record ProviderRouteIdentity(
    string ProviderId,
    string AccountId,
    string OperationId,
    string VoiceId,
    string PricingProvenanceSha256,
    string CapabilityProvenanceSha256)
{
    public ProviderRouteIdentity Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(OperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(VoiceId);
        ValidateHash(PricingProvenanceSha256, nameof(PricingProvenanceSha256));
        ValidateHash(CapabilityProvenanceSha256, nameof(CapabilityProvenanceSha256));
        return this;
    }

    private static void ValidateHash(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("Expected a SHA-256 hexadecimal value.", name);
    }
}

public sealed record ProviderRouteFallbackAuthorization(
    ProviderRouteIdentity PinnedRoute,
    ProviderRouteIdentity AllowedFallback,
    string Currency,
    int CurrencyScale,
    long MaximumFallbackMinorUnits)
{
    public ProviderRouteFallbackAuthorization Validate()
    {
        PinnedRoute.Validate();
        AllowedFallback.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(Currency);
        if (CurrencyScale is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(CurrencyScale));
        if (MaximumFallbackMinorUnits < 0) throw new ArgumentOutOfRangeException(nameof(MaximumFallbackMinorUnits));
        if (PinnedRoute == AllowedFallback)
            throw new InvalidOperationException("Fallback authorization must identify a distinct route.");
        return this;
    }
}

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
        if (billedCurrencyScale is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(billedCurrencyScale));
        if (projectedFallbackMinorUnits < 0) throw new ArgumentOutOfRangeException(nameof(projectedFallbackMinorUnits));

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
