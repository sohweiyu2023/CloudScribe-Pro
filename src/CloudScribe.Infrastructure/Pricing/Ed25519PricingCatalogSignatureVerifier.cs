using CloudScribe.Application.Pricing;
using Microsoft.Extensions.Options;
using NSec.Cryptography;

namespace CloudScribe.Infrastructure.Pricing;

public sealed class Ed25519PricingCatalogSignatureVerifier : IPricingCatalogSignatureVerifier
{
    private readonly Dictionary<string, string> _trustedPublicKeys;

    public Ed25519PricingCatalogSignatureVerifier(IOptions<PricingCatalogTrustOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IDictionary<string, string>? configuredKeys = options.Value.TrustedEd25519PublicKeys;
        _trustedPublicKeys = configuredKeys is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(configuredKeys, StringComparer.Ordinal);
    }

    public PricingCatalogSignatureVerification Verify(ReadOnlyMemory<byte> catalogBytes, PricingCatalogSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (catalogBytes.IsEmpty)
        {
            throw new ArgumentException("Pricing catalog bytes cannot be empty.", nameof(catalogBytes));
        }

        SignatureAlgorithm algorithm = SignatureAlgorithm.Ed25519;
        if (signature.SignatureBytes.Length != algorithm.SignatureSize)
        {
            return PricingCatalogSignatureVerification.Rejected("Detached Ed25519 signature has an invalid length.");
        }

        if (!_trustedPublicKeys.TryGetValue(signature.KeyId, out string? encodedPublicKey))
        {
            return PricingCatalogSignatureVerification.Rejected("Detached signature key ID is not present in the externally configured trusted-key set.");
        }

        if (!TryDecodePublicKey(encodedPublicKey, algorithm.PublicKeySize, out byte[] publicKeyBytes))
        {
            return PricingCatalogSignatureVerification.Rejected("Externally configured Ed25519 public key is malformed.");
        }

        try
        {
            if (!PublicKey.TryImport(algorithm, publicKeyBytes, KeyBlobFormat.RawPublicKey, out PublicKey? publicKey) || publicKey is null)
            {
                return PricingCatalogSignatureVerification.Rejected("Externally configured Ed25519 public key cannot be imported.");
            }

            return algorithm.Verify(publicKey, catalogBytes.Span, signature.SignatureBytes.Span)
                ? PricingCatalogSignatureVerification.Verified()
                : PricingCatalogSignatureVerification.Rejected("Detached Ed25519 signature does not match the exact catalog bytes.");
        }
        finally
        {
            Array.Clear(publicKeyBytes);
        }
    }

    private static bool TryDecodePublicKey(string? encodedPublicKey, int requiredLength, out byte[] publicKeyBytes)
    {
        publicKeyBytes = [];
        if (string.IsNullOrWhiteSpace(encodedPublicKey))
        {
            return false;
        }

        try
        {
            byte[] decoded = Convert.FromBase64String(encodedPublicKey);
            if (decoded.Length != requiredLength)
            {
                Array.Clear(decoded);
                return false;
            }

            publicKeyBytes = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
