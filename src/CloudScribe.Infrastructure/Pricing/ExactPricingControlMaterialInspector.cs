using System.Security.Cryptography;

namespace CloudScribe.Infrastructure.Pricing;

public sealed class ExactPricingControlMaterialInspector
{
    private readonly StrictJsonObjectReader _reader;

    public ExactPricingControlMaterialInspector(StrictJsonObjectReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    public Inspection Inspect(ReadOnlyMemory<byte> utf8Json, string expectedSha256)
    {
        string expected = NormalizeExpectedSha256(expectedSha256);
        string actual = Convert.ToHexString(SHA256.HashData(utf8Json.Span)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return new Inspection(
                false,
                actual,
                null,
                "Control material identity does not match the authenticated expected SHA-256; parsing/admission is blocked.");
        }

        try
        {
            using var document = _reader.Parse(utf8Json);
            return new Inspection(
                true,
                actual,
                null,
                "Control material identity matches and the bytes are strict UTF-8 JSON with an object root. Contract/schema admission remains a separate gate.");
        }
        catch (PricingCatalogFormatException exception)
        {
            return new Inspection(
                true,
                actual,
                exception.Error,
                $"Control material identity matches, but strict JSON intake failed: {exception.Error}.");
        }
    }

    private static string NormalizeExpectedSha256(string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        string value = expectedSha256.Trim().ToLowerInvariant();
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Expected SHA-256 must contain exactly 64 hexadecimal characters.",
                nameof(expectedSha256));
        }

        return value;
    }

    public sealed record Inspection(
        bool IdentityMatched,
        string ActualSha256,
        PricingCatalogFormatError? FormatError,
        string StatusReason)
    {
        public bool StrictJsonObjectAccepted => IdentityMatched && FormatError is null;
    }
}
