namespace CloudScribe.Infrastructure.Pricing;

public sealed class PricingCatalogFormatException : FormatException
{
    public PricingCatalogFormatException(
        PricingCatalogFormatError error,
        string message,
        long? bytePosition = null,
        string? propertyName = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
        BytePosition = bytePosition;
        PropertyName = propertyName;
    }

    public PricingCatalogFormatError Error { get; }
    public long? BytePosition { get; }
    public string? PropertyName { get; }
}
