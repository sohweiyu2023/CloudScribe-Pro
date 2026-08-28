namespace CloudScribe.Application.Pricing;

public sealed record PricingCatalogSignature
{
    public PricingCatalogSignature(string keyId, ReadOnlyMemory<byte> signatureBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (signatureBytes.IsEmpty)
        {
            throw new ArgumentException("Detached catalog signature cannot be empty.", nameof(signatureBytes));
        }
        KeyId = keyId.Trim();
        SignatureBytes = signatureBytes.ToArray();
    }

    public string KeyId { get; }
    public ReadOnlyMemory<byte> SignatureBytes { get; }
}
