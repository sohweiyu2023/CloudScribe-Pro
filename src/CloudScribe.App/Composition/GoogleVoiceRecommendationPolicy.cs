using System.Globalization;
using System.Text.Json;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.App.Composition;

/// <summary>Local catalog ordering only. Never discovers voices, persists preferences or submits speech.</summary>
public static class GoogleVoiceRecommendationPolicy
{
    public static VoiceLabCatalogSelection? Recommend(
        IEnumerable<VoiceLabCatalogSelection> catalog, string? locale, bool premiumHd = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(locale)) return null;
        string requested = locale.Trim();
        return catalog.Where(voice => IsEligible(voice, requested, premiumHd))
            .OrderBy(voice => Rank(voice.VoiceStableId, requested, premiumHd))
            .ThenBy(voice => voice.VoiceStableId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static bool MayApplyDefault(VoiceLabCatalogSelection? recommendation,
        VoiceLabCatalogSelection? selected, string? rememberedVoice, string? locale, bool premiumHd, bool priceUsable) =>
        recommendation is not null && selected is null && string.IsNullOrWhiteSpace(rememberedVoice)
        && locale?.StartsWith("en-", StringComparison.OrdinalIgnoreCase) == true && !premiumHd && priceUsable;

    public static RecommendationPrice? ReferencePrice(V222ControlSet controls, string voiceName)
    {
        ArgumentNullException.ThrowIfNull(controls);
        GoogleVoiceComparisonInformation.CharacterRate? price = GoogleVoiceComparisonInformation.Rate(controls, voiceName);
        if (price is null) return null;
        using JsonDocument json = JsonDocument.Parse(controls.PricingSeedUtf8);
        JsonElement rate = json.RootElement.GetProperty("rates").EnumerateArray()
            .Single(row => string.Equals(row.GetProperty("rate_id").GetString(), price.RateId, StringComparison.Ordinal));
        return new RecommendationPrice(price.UsdPerMillion, rate.GetProperty("verified_at_utc").GetDateTimeOffset(),
            json.RootElement.GetProperty("import_policy").GetProperty("max_rate_age_days").GetInt32());
    }

    private static bool IsEligible(VoiceLabCatalogSelection voice, string locale, bool premiumHd)
    {
        if (!string.Equals(voice.ProviderStableId, GoogleGenerationProvider.StableProviderId, StringComparison.Ordinal)
            || voice.LanguageCodes?.Contains(locale, StringComparer.OrdinalIgnoreCase) != true) return false;
        try { voice.Validate(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return false; }
        string family = GoogleVoiceComparisonInformation.Family(voice.VoiceStableId).ToLowerInvariant();
        if (locale.StartsWith("cmn-", StringComparison.OrdinalIgnoreCase) && family is "neural2") return false;
        // Recommendation scope is the tested plain-text, default-controls, short MP3 path.
        return premiumHd ? family is "chirp 3 hd" : family is "neural2" or "wavenet" or "standard";
    }

    private static int Rank(string name, string locale, bool premiumHd)
    {
        if (premiumHd)
            return string.Equals(name, "cmn-CN-Chirp3-HD-Alnilam", StringComparison.Ordinal) ? 0 : 1;
        string family = GoogleVoiceComparisonInformation.Family(name).ToLowerInvariant();
        if (locale.StartsWith("en-", StringComparison.OrdinalIgnoreCase) && family is "neural2") return 0;
        return family switch { "wavenet" => 1, "neural2" => 2, "standard" => 3, _ => 4 };
    }

    public sealed record RecommendationPrice(decimal UsdPerMillion, DateTimeOffset VerifiedAtUtc, int MaximumAgeDays)
    {
        public bool IsUsable(DateTimeOffset nowUtc) => UsdPerMillion >= 0 && MaximumAgeDays > 0
            && VerifiedAtUtc <= nowUtc && nowUtc - VerifiedAtUtc <= TimeSpan.FromDays(MaximumAgeDays);
        public string Describe(DateTimeOffset nowUtc) => IsUsable(nowUtc)
            ? "Reference USD " + UsdPerMillion.ToString("0.##", CultureInfo.InvariantCulture)
                + " / million characters; catalog checked " + VerifiedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                + ". Current exact-request cost review is still required."
            : "Reference pricing is stale or invalid; no current price is asserted. Review pricing before generation.";
    }
}

