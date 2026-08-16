namespace CloudScribe.Application.Pricing;

public sealed record PricingCatalogContractValidation
{
    public PricingCatalogContractValidation(bool contractAvailable, IEnumerable<PricingCatalogDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        Diagnostics = diagnostics.ToArray();
        ContractAvailable = contractAvailable;
    }

    public bool ContractAvailable { get; }
    public IReadOnlyList<PricingCatalogDiagnostic> Diagnostics { get; }
    public bool IsValid => ContractAvailable && Diagnostics.Count == 0;

    public static PricingCatalogContractValidation Valid() => new(true, []);
}
