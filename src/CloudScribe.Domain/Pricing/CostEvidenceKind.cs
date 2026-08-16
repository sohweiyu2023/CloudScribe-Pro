namespace CloudScribe.Domain.Pricing;

public enum CostEvidenceKind
{
    Unknown = 0,
    Estimated = 1,
    Quoted = 2,
    ProviderReported = 3,
    ReconciledInvoice = 4,
}
