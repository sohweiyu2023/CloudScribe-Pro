namespace CloudScribe.Domain.Generation;

public sealed record ContentAddressedSegmentKey(string Sha256)
{
    public string PrivateLookupHmacSha256 => Sha256;

    public ContentAddressedSegmentKey Validate()
    {
        ValidateSha256(Sha256);
        return this;
    }

    private static void ValidateSha256(string sha256)
    {
        if (sha256.Length != 64 || sha256.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Segment cache lookup key must be a 64-character private HMAC-SHA-256 hex digest.", nameof(sha256));
        }
    }

    public static ContentAddressedSegmentKey FromPrivateLookup(PrivateCacheLookupKey lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        lookup.Validate();
        return new ContentAddressedSegmentKey(lookup.HmacSha256).Validate();
    }

    [Obsolete("v2.23 requires an OS-protected private HMAC cache namespace with a complete trust context. Use PrivateCacheLookupKey.Derive and FromPrivateLookup instead.")]
    public static ContentAddressedSegmentKey Create(
        ReadOnlySpan<byte> compiledPayload,
        string providerStableId,
        string operationStableId,
        string voiceStableId,
        string compilationProfileId) =>
        throw new InvalidOperationException("Raw deterministic cache lookup identifiers are prohibited by CloudScribe v2.23 CACHE-001/ARCH-014.");
}
