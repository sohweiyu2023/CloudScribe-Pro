using System.Globalization;

namespace CloudScribe.Domain.Safety;

public static class CachePrivacyUserPolicy
{
    public const bool IncludeCacheInProjectExportsByDefault = false;

    public const bool ClaimSecureEraseOnNormalClear = false;

    public const string ClearCacheWarning =
        "Clearing reusable generated-audio cache can cause future generation to contact providers again and may incur new provider charges. Active, pinned, referenced, or unresolved-submission entries remain protected from normal cache clearing.";

    public static bool MayIncludeCacheInProjectBundle(
        bool userExplicitlyRequestedCacheExport,
        bool currentPolicyAllowsCacheExport) =>
        userExplicitlyRequestedCacheExport && currentPolicyAllowsCacheExport;

    public static string DescribeEstimatedCostAvoidance(
        string currencyCode,
        long scaledUnits,
        int scale)
    {
        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3 ||
            currencyCode.Any(static character => character is < 'A' or > 'Z'))
        {
            throw new ArgumentException("Currency must be a three-letter uppercase provider-billed code.", nameof(currencyCode));
        }
        if (scaledUnits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scaledUnits));
        }
        if (scale is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        var divisor = Pow10(scale);
        var amount = scaledUnits / divisor;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Estimated provider-billed cost avoided by eligible cache reuse: {currencyCode} {amount:F{scale}}. This is an estimate, not a guarantee of money saved.");
    }

    private static decimal Pow10(int scale)
    {
        var value = 1m;
        for (var index = 0; index < scale; index++) value *= 10m;
        return value;
    }
}
