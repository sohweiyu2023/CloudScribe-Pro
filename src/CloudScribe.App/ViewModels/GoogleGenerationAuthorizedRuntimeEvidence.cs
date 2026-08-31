using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.ViewModels;

public sealed record GoogleGenerationAuthorizedRuntimeEvidence(
    GoogleGenerationHttpTransport Transport,
    GoogleGenerationAccount Account,
    GoogleCapabilitySnapshot Capabilities,
    GoogleGenerationSpendAuthorization SpendAuthorization,
    string PricingProvenanceId,
    int RequestRevision,
    string Currency,
    int Scale,
    long CurrentEstimateMinorUnits,
    GoogleGenerationUiExecutionSnapshot Snapshot);
