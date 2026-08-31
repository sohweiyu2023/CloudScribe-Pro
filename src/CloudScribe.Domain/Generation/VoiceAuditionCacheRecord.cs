namespace CloudScribe.Domain.Generation;

public sealed record VoiceAuditionCacheRecord(
    string ProviderStableId,
    string AccountId,
    string VoiceStableId,
    string PricingProvenanceId,
    string SampleTextSha256,
    string MediaFormat,
    string MediaSha256,
    TimeSpan Duration)
{
    public static VoiceAuditionCacheRecord Create(
        VoiceAuditionAuthorization authorization,
        string mediaFormat,
        string mediaSha256,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFormat);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaSha256);
        if (mediaSha256.Length != 64 || mediaSha256.Any(static c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Audition media SHA-256 must be exactly 64 hexadecimal characters.", nameof(mediaSha256));
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(duration));

        return new VoiceAuditionCacheRecord(
            authorization.ProviderStableId,
            authorization.AccountId,
            authorization.VoiceStableId,
            authorization.PricingProvenanceId,
            authorization.SampleTextSha256,
            mediaFormat.Trim().ToLowerInvariant(),
            mediaSha256.ToLowerInvariant(),
            duration);
    }

    public bool CanReuse(VoiceAuditionAuthorization authorization, string mediaFormat)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFormat);

        return string.Equals(ProviderStableId, authorization.ProviderStableId, StringComparison.Ordinal) &&
            string.Equals(AccountId, authorization.AccountId, StringComparison.Ordinal) &&
            string.Equals(VoiceStableId, authorization.VoiceStableId, StringComparison.Ordinal) &&
            string.Equals(PricingProvenanceId, authorization.PricingProvenanceId, StringComparison.Ordinal) &&
            string.Equals(SampleTextSha256, authorization.SampleTextSha256, StringComparison.Ordinal) &&
            string.Equals(MediaFormat, mediaFormat.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public string CacheKey => $"audition:{ProviderStableId}:{AccountId}:{VoiceStableId}:{PricingProvenanceId}:{SampleTextSha256}:{MediaFormat}";
}
