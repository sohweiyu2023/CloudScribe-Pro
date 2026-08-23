using System.Security.Cryptography;
using System.Text;

namespace CloudScribe.Domain.Generation;

public sealed record GenerationCacheTrustContext(
    string ProviderStableId,
    string AccountId,
    string ProjectId,
    string EndpointId,
    string RegionId,
    string OperationStableId,
    string ResolvedModelId,
    string VoiceStableId,
    string VoiceFingerprint,
    string SpeechPlanIdentity,
    string LanguageTag,
    string SynthesisControlsIdentity,
    string OutputFormat,
    string SampleFormatIdentity,
    string AdapterVersion,
    string CompilerVersion,
    string AstVersion,
    string NormalizationVersion,
    string PricingIdentity,
    string CapabilityIdentity,
    string GovernancePolicyIdentity,
    string ProviderFeatureIdentity,
    string AccountCapabilityIdentity)
{
    public GenerationCacheTrustContext Validate()
    {
        foreach (var value in Values())
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Every cache trust-namespace field must be explicit. Use a stable 'none' token when a field is not applicable.");
            }
        }

        return this;
    }

    public IEnumerable<string> Values()
    {
        yield return ProviderStableId;
        yield return AccountId;
        yield return ProjectId;
        yield return EndpointId;
        yield return RegionId;
        yield return OperationStableId;
        yield return ResolvedModelId;
        yield return VoiceStableId;
        yield return VoiceFingerprint;
        yield return SpeechPlanIdentity;
        yield return LanguageTag;
        yield return SynthesisControlsIdentity;
        yield return OutputFormat;
        yield return SampleFormatIdentity;
        yield return AdapterVersion;
        yield return CompilerVersion;
        yield return AstVersion;
        yield return NormalizationVersion;
        yield return PricingIdentity;
        yield return CapabilityIdentity;
        yield return GovernancePolicyIdentity;
        yield return ProviderFeatureIdentity;
        yield return AccountCapabilityIdentity;
    }
}

public sealed record PrivateCacheLookupKey(string HmacSha256)
{
    public PrivateCacheLookupKey Validate()
    {
        if (HmacSha256.Length != 64 || HmacSha256.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Private cache lookup identifiers must be 64-character HMAC-SHA-256 hex digests.", nameof(HmacSha256));
        }

        return this;
    }

    public static PrivateCacheLookupKey Derive(
        ReadOnlySpan<byte> hmacKey,
        GenerationCacheTrustContext trustContext,
        ReadOnlySpan<byte> compiledPayload)
    {
        ArgumentNullException.ThrowIfNull(trustContext);
        trustContext.Validate();
        if (hmacKey.Length < 32)
        {
            throw new ArgumentException("Cache HMAC key material must contain at least 256 bits.", nameof(hmacKey));
        }

        using var buffer = new MemoryStream();
        Append(buffer, "cloudscribe-private-cache-v2.23");
        foreach (var value in trustContext.Values())
        {
            Append(buffer, value);
        }
        AppendLength(buffer, compiledPayload.Length);
        buffer.Write(compiledPayload);

        var canonical = buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length));
        var digest = HMACSHA256.HashData(hmacKey, canonical);
        try
        {
            return new PrivateCacheLookupKey(Convert.ToHexString(digest).ToLowerInvariant());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    public static string ComputeHmacSha256Hex(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        var digest = HMACSHA256.HashData(key, data);
        try
        {
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static void Append(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            AppendLength(stream, bytes.Length);
            stream.Write(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void AppendLength(Stream stream, int length)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lengthBytes, length);
        stream.Write(lengthBytes);
    }
}
