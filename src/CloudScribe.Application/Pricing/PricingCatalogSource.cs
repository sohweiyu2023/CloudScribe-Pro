namespace CloudScribe.Application.Pricing;

public sealed record PricingCatalogSource
{
    public PricingCatalogSource(PricingCatalogSourceKind kind, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        string normalized = label.Trim();
        if (normalized.Length > 240 || normalized.Any(static character => char.IsControl(character)))
        {
            throw new ArgumentException("Catalog source label must be 1-240 visible characters.", nameof(label));
        }

        Kind = kind;
        Label = normalized;
    }

    public PricingCatalogSourceKind Kind { get; }
    public string Label { get; }
}
