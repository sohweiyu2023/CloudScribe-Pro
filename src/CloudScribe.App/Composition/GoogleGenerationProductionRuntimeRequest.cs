using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed record GoogleGenerationProductionRuntimeRequest(
    string AccountId,
    GoogleGenerationSubmissionEnvelope SubmissionEnvelope,
    string PricingProvenanceId,
    int RequestRevision,
    string Currency,
    int Scale,
    long CurrentEstimateMinorUnits,
    GoogleGenerationUiExecutionSnapshot Snapshot)
{
    public GoogleGenerationProductionRuntimeRequest Validate()
    {
        RequireCanonical(AccountId, nameof(AccountId));
        ArgumentNullException.ThrowIfNull(SubmissionEnvelope);
        RequireCanonical(PricingProvenanceId, nameof(PricingProvenanceId));
        RequireCanonical(Currency, nameof(Currency));
        ArgumentNullException.ThrowIfNull(Snapshot);

        if (RequestRevision < 0)
            throw new InvalidOperationException("Google generation request revision cannot be negative.");
        if (Scale is < 0 or > 9)
            throw new InvalidOperationException("Google generation currency scale must be between zero and nine.");
        if (CurrentEstimateMinorUnits < 0)
            throw new InvalidOperationException("Google generation current estimate cannot be negative.");
        if (!string.Equals(AccountId, SubmissionEnvelope.AccountId, StringComparison.Ordinal))
            throw new InvalidOperationException("Google runtime request account differs from the durable submission envelope.");

        return this;
    }

    private static void RequireCanonical(string value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Google runtime request '{propertyName}' is required.");
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
        {
            throw new InvalidOperationException($"Google runtime request '{propertyName}' must be canonical.");
        }
    }
}
