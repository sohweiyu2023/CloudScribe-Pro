using System.Globalization;
using System.Text;
using System.Text.Json;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.App.Composition;

public static class GoogleVoiceComparisonInformation
{
    public const string CatalogUrl = "https://docs.cloud.google.com/text-to-speech/docs/list-voices-and-types";
    public const string PricingUrl = "https://cloud.google.com/text-to-speech/pricing";
    public const string ReferenceChecked = "2026-10-20";
    public const string DefaultText = "Awareness is vivid and clear. There is thinking, no thinker. In hearing, just sounds. Let each sentence unfold naturally, with a quiet pause.";
    public static IReadOnlyList<string> SuggestedVoices { get; } =
        ["en-US-Standard-C", "en-US-Wavenet-C", "en-US-Neural2-C", "en-US-Neural2-D", "en-US-Standard-D"];

    public static string Family(string name)
    {
        if (name.Contains("-Chirp3-HD-", StringComparison.OrdinalIgnoreCase)) return "Chirp 3 HD";
        string[] pieces = name.Split('-');
        return pieces.Length > 2 ? pieces[2] : "Unknown";
    }

    public static bool SupportsSampleFamily(string name) => Family(name) is "Standard" or "Wavenet" or "Neural2";

    public static string Description(string name) => Family(name) switch
    {
        "Standard" => "Standard synthesis family. Low reference price; listen to judge whether its delivery suits your text.",
        "Wavenet" => "WaveNet family. Its checked character price matches Standard; compare the delivery rather than assuming a higher price.",
        "Neural2" => "Neural2 family. Higher character price; naturalness and pronunciation still need listening comparison.",
        "Studio" => "Studio family. Substantially higher list price; official samples only in this focused comparison panel.",
        "Chirp 3 HD" => "HD family with different request capabilities. Official samples only; this panel does not assume compatible prosody settings.",
        _ => "Catalog/reference entry. No compatible priced short-sample workflow is asserted for this family.",
    };

    public static string LocaleLabel(string locale) => locale switch
    {
        "en-US" => "English (United States)",
        "en-GB" => "English (United Kingdom)",
        "en-AU" => "English (Australia)",
        "en-IN" => "English (India)",
        "en-CA" => "English (Canada)",
        _ => locale,
    };

    public static CharacterRate? Rate(V222ControlSet controls, string voice)
    {
        string? model = Family(voice) switch
        {
            "Standard" => "google.standard",
            "Wavenet" => "google.wavenet",
            "Neural2" => "google.neural2",
            "Studio" => "google.studio",
            "Chirp 3 HD" => "google.chirp-3-hd",
            _ => null,
        };
        if (model is null) return null;
        using JsonDocument document = JsonDocument.Parse(controls.PricingSeedUtf8);
        JsonElement[] rates = document.RootElement.GetProperty("rates").EnumerateArray().Where(r =>
            string.Equals(r.GetProperty("provider_id").GetString(), "google-cloud", StringComparison.Ordinal) &&
            r.GetProperty("model_ids").EnumerateArray().Any(id => string.Equals(id.GetString(), model, StringComparison.Ordinal)) &&
            string.Equals(r.GetProperty("region_id").GetString(), "global", StringComparison.Ordinal) &&
            string.Equals(r.GetProperty("currency").GetString(), "USD", StringComparison.Ordinal) &&
            string.Equals(r.GetProperty("billing_unit").GetString(), "characters", StringComparison.Ordinal) &&
            string.Equals(r.GetProperty("billing_component").GetString(), "synthesis", StringComparison.Ordinal) &&
            string.Equals(r.GetProperty("pricing_state").GetString(), "exact", StringComparison.Ordinal)).ToArray();
        if (rates.Length != 1) return null;
        decimal price = rates[0].GetProperty("price").GetDecimal();
        long quantity = rates[0].GetProperty("unit_quantity").GetInt64();
        if (quantity <= 0 || price < 0) return null;
        return new CharacterRate(price * 1_000_000m / quantity, rates[0].GetProperty("rate_id").GetString()!);
    }

    public sealed record CharacterRate(decimal UsdPerMillion, string RateId)
    {
        public string PriceLabel => "USD " + UsdPerMillion.ToString("0.##", CultureInfo.InvariantCulture) + " / 1 million characters";
        public decimal ListEstimate(string text) => text.EnumerateRunes().LongCount() * UsdPerMillion / 1_000_000m;
        public long ConservativeCents(string text) => checked((long)decimal.Ceiling(Encoding.UTF8.GetByteCount(text) * UsdPerMillion / 10_000m));
    }
}
