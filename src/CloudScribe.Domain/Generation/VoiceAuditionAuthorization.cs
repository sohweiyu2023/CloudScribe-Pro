using System.Security.Cryptography;
using System.Text;

namespace CloudScribe.Domain.Generation;

public sealed record VoiceAuditionAuthorization(
    string ProviderStableId,
    string AccountId,
    string VoiceStableId,
    string PricingProvenanceId,
    string Currency,
    long MaximumScaledAmount,
    int Scale,
    string SampleTextSha256)
{
    public static VoiceAuditionAuthorization Create(
        string providerStableId,
        string accountId,
        string voiceStableId,
        string pricingProvenanceId,
        string currency,
        long maximumScaledAmount,
        int scale,
        string sampleText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(sampleText);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumScaledAmount);
        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, 12);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sampleText))).ToLowerInvariant();
        return new VoiceAuditionAuthorization(
            providerStableId,
            accountId,
            voiceStableId,
            pricingProvenanceId,
            currency.ToUpperInvariant(),
            maximumScaledAmount,
            scale,
            hash);
    }

    public void EnsureAuthorized(
        string providerStableId,
        string accountId,
        string voiceStableId,
        string pricingProvenanceId,
        string currency,
        long projectedScaledAmount,
        int scale,
        string sampleText)
    {
        var current = Create(
            providerStableId,
            accountId,
            voiceStableId,
            pricingProvenanceId,
            currency,
            projectedScaledAmount,
            scale,
            sampleText);

        if (!string.Equals(ProviderStableId, current.ProviderStableId, StringComparison.Ordinal) ||
            !string.Equals(AccountId, current.AccountId, StringComparison.Ordinal) ||
            !string.Equals(VoiceStableId, current.VoiceStableId, StringComparison.Ordinal) ||
            !string.Equals(PricingProvenanceId, current.PricingProvenanceId, StringComparison.Ordinal) ||
            !string.Equals(Currency, current.Currency, StringComparison.Ordinal) ||
            Scale != current.Scale ||
            !string.Equals(SampleTextSha256, current.SampleTextSha256, StringComparison.Ordinal) ||
            projectedScaledAmount > MaximumScaledAmount)
        {
            throw new InvalidOperationException("Voice audition no longer matches its exact approved route, sample, pricing provenance, currency, scale or spend ceiling.");
        }
    }

    public string CacheIdentity => $"{ProviderStableId}:{AccountId}:{VoiceStableId}:{PricingProvenanceId}:{SampleTextSha256}";
}
