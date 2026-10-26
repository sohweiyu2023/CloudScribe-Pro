using System.Buffers;
using System.Text;
using System.Text.Json;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.App.Composition;

/// <summary>Conservative provider-list subtotal from authenticated rates and exact compiled input; no tax, credit or FX assertion.</summary>
public static class GoogleTtsStudioCostEstimator
{
    public static EstimateResult Estimate(V222ControlSet controls, string voiceName, ReadOnlyMemory<byte> compiledPayload, string region)
    {
        ArgumentNullException.ThrowIfNull(controls);
        string model = GoogleTtsVoiceBillingFamilyClassifier.Classify(voiceName) switch
        {
            GoogleTtsVoiceBillingFamilyClassifier.Standard => "google.standard",
            GoogleTtsVoiceBillingFamilyClassifier.WaveNet => "google.wavenet",
            GoogleTtsVoiceBillingFamilyClassifier.Neural2 => "google.neural2",
            GoogleTtsVoiceBillingFamilyClassifier.Studio => "google.studio",
            GoogleTtsVoiceBillingFamilyClassifier.Chirp3Hd => "google.chirp-3-hd",
            _ => throw new InvalidOperationException("No admitted character-rate model is available for the selected voice family."),
        };
        using JsonDocument catalog = JsonDocument.Parse(controls.PricingSeedUtf8);
        JsonElement[] rates = catalog.RootElement.GetProperty("rates").EnumerateArray().Where(rate =>
            string.Equals(rate.GetProperty("provider_id").GetString(), "google-cloud", StringComparison.Ordinal) &&
            rate.GetProperty("model_ids").EnumerateArray().Any(id => string.Equals(id.GetString(), model, StringComparison.Ordinal)) &&
            string.Equals(rate.GetProperty("region_id").GetString(), region, StringComparison.Ordinal) &&
            string.Equals(rate.GetProperty("billing_component").GetString(), "synthesis", StringComparison.Ordinal) &&
            string.Equals(rate.GetProperty("billing_unit").GetString(), "characters", StringComparison.Ordinal) &&
            string.Equals(rate.GetProperty("pricing_state").GetString(), "exact", StringComparison.Ordinal)).ToArray();
        if (rates.Length != 1 || !string.Equals(rates[0].GetProperty("currency").GetString(), "USD", StringComparison.Ordinal))
            throw new InvalidOperationException("One exact admitted USD character rate is required; no price or exchange rate will be guessed.");
        decimal price = rates[0].GetProperty("price").GetDecimal();
        long quantity = rates[0].GetProperty("unit_quantity").GetInt64();
        if (price < 0 || quantity <= 0)
            throw new InvalidOperationException("The admitted character rate is invalid.");
        using JsonDocument payload = JsonDocument.Parse(compiledPayload);
        JsonElement input = payload.RootElement.GetProperty("input");
        bool isChirp = string.Equals(model, "google.chirp-3-hd", StringComparison.Ordinal);
        bool hasSsml = input.TryGetProperty("ssml", out JsonElement ssml);
        if (isChirp && hasSsml)
            throw new InvalidOperationException("Chirp 3 HD estimates require the supported plain-text request, not SSML.");
        string? text = hasSsml ? ssml.GetString() : input.GetProperty("text").GetString();
        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException("Compiled Google input is empty.");
        // The byte envelope limit remains independent of Google's character billing unit.
        // Chirp's supported path is plain text: count Unicode scalars, including whitespace,
        // without normalization or grapheme merging. Retain existing conservative handling for
        // other families/SSML, where markup (including mark tags) remains in the upper bound.
        long inputBytes = Encoding.UTF8.GetByteCount(text);
        long billingCharacters = isChirp ? CountUnicodeScalars(text) : inputBytes;
        // Ignore allowances/credits and retain an upward cent ceiling for EACH request.
        long minorUnits = checked((long)decimal.Ceiling(checked(price * billingCharacters * 100m) / quantity));
        return new EstimateResult("USD", 2, minorUnits, inputBytes, rates[0].GetProperty("rate_id").GetString()!)
        { EstimatedBillableCharacters = billingCharacters };
    }

    private static long CountUnicodeScalars(string text)
    {
        ReadOnlySpan<char> remaining = text.AsSpan();
        long count = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out int consumed) != OperationStatus.Done)
                throw new InvalidOperationException("Google billing input contains an unpaired Unicode surrogate.");
            count = checked(count + 1);
            remaining = remaining[consumed..];
        }
        return count;
    }

    public sealed record EstimateResult(string Currency, int Scale, long MaximumMinorUnits, long InputUtf8Bytes, string RateId)
    {
        public long EstimatedBillableCharacters { get; init; }
    }
}
