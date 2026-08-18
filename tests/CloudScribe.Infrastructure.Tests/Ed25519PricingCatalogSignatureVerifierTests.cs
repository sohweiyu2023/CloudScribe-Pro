using CloudScribe.Application.Pricing;
using CloudScribe.Infrastructure.Pricing;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Ed25519PricingCatalogSignatureVerifierTests
{
    private const string TrustedKeyId = "rfc8032-test-key";
    private const string PublicKeyHex = "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c";
    private const string SignatureHex = "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00";
    private static readonly byte[] Rfc8032Message = [0x72];
    private static readonly byte[] TamperedMessage = [0x73];

    [Fact]
    public void Rfc8032VectorVerifiesAgainstExternallyConfiguredTrustedKey()
    {
        Ed25519PricingCatalogSignatureVerifier verifier = CreateVerifier(TrustedKeyId, Convert.ToBase64String(Convert.FromHexString(PublicKeyHex)));

        PricingCatalogSignatureVerification result = verifier.Verify(Rfc8032Message, Signature());

        Assert.True(result.IsVerified);
        Assert.Contains("externally trusted key", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyTrustedKeySetFailsClosed()
    {
        Ed25519PricingCatalogSignatureVerifier verifier = CreateVerifier();

        PricingCatalogSignatureVerification result = verifier.Verify(Rfc8032Message, Signature());

        Assert.False(result.IsVerified);
        Assert.Contains("not present", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownKeyIdFailsClosedEvenWhenAnotherKeyIsTrusted()
    {
        Ed25519PricingCatalogSignatureVerifier verifier = CreateVerifier(TrustedKeyId, Convert.ToBase64String(Convert.FromHexString(PublicKeyHex)));
        PricingCatalogSignature signature = new("other-key", Convert.FromHexString(SignatureHex));

        PricingCatalogSignatureVerification result = verifier.Verify(Rfc8032Message, signature);

        Assert.False(result.IsVerified);
        Assert.Contains("not present", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeyIdsAreMatchedOrdinallyWithoutCaseFolding()
    {
        Ed25519PricingCatalogSignatureVerifier verifier = CreateVerifier(TrustedKeyId, Convert.ToBase64String(Convert.FromHexString(PublicKeyHex)));
        PricingCatalogSignature signature = new(TrustedKeyId.ToUpperInvariant(), Convert.FromHexString(SignatureHex));

        PricingCatalogSignatureVerification result = verifier.Verify(Rfc8032Message, signature);

        Assert.False(result.IsVerified);
    }

    [Fact]
    public void TamperedCatalogBytesFailVerification()
    {
        Ed25519PricingCatalogSignatureVerifier verifier = CreateVerifier(TrustedKeyId, Convert.ToBase64String(Convert.FromHexString(PublicKeyHex)));

        PricingCatalogSignatureVerification result = verifier.Verify(TamperedMessage, Signature());

        Assert.False(result.IsVerified);
        Assert.Contains("does not match", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TamperedSignatureFailsVerification()
    {
        Ed25519PricingCatalogSignatureVerifier verifier = CreateVerifier(TrustedKeyId, Convert.ToBase64String(Convert.FromHexString(PublicKeyHex)));
        byte[] signatureBytes = Convert.FromHexString(SignatureHex);
        signatureBytes[0] ^= 0x01;

        PricingCatalogSignatureVerification result = verifier.Verify(Rfc8032Message, new PricingCatalogSignature(TrustedKeyId, signatureBytes));

        Assert.False(result.IsVerified);
    }

    [Fact]
    public void MalformedConfiguredPublicKeyFailsClosed()
    {
        Ed25519PricingCatalogSignatureVerifier verifier = CreateVerifier(TrustedKeyId, "not-base64!");

        PricingCatalogSignatureVerification result = verifier.Verify(Rfc8032Message, Signature());

        Assert.False(result.IsVerified);
        Assert.Contains("malformed", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongLengthConfiguredPublicKeyFailsClosed()
    {
        Ed25519PricingCatalogSignatureVerifier verifier = CreateVerifier(TrustedKeyId, Convert.ToBase64String(new byte[31]));

        PricingCatalogSignatureVerification result = verifier.Verify(Rfc8032Message, Signature());

        Assert.False(result.IsVerified);
        Assert.Contains("malformed", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongLengthSignatureFailsClosedBeforeCryptoVerification()
    {
        Ed25519PricingCatalogSignatureVerifier verifier = CreateVerifier(TrustedKeyId, Convert.ToBase64String(Convert.FromHexString(PublicKeyHex)));
        PricingCatalogSignature signature = new(TrustedKeyId, new byte[63]);

        PricingCatalogSignatureVerification result = verifier.Verify(Rfc8032Message, signature);

        Assert.False(result.IsVerified);
        Assert.Contains("invalid length", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static PricingCatalogSignature Signature() => new(TrustedKeyId, Convert.FromHexString(SignatureHex));

    private static Ed25519PricingCatalogSignatureVerifier CreateVerifier(string? keyId = null, string? encodedPublicKey = null)
    {
        PricingCatalogTrustOptions options = new();
        if (keyId is not null && encodedPublicKey is not null)
        {
            options.TrustedEd25519PublicKeys.Add(keyId, encodedPublicKey);
        }

        return new Ed25519PricingCatalogSignatureVerifier(Options.Create(options));
    }
}
