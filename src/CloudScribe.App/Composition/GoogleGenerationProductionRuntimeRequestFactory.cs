using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public static class GoogleGenerationProductionRuntimeRequestFactory
{
    public static GoogleGenerationProductionRuntimeRequest Create(
        GoogleGenerationSpendAuthorization authorization,
        GoogleGenerationUiExecutionSnapshot snapshot,
        long currentEstimateMinorUnits)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegative(currentEstimateMinorUnits);

        GoogleGenerationSubmissionEnvelope envelope = authorization.Envelope
            ?? throw new InvalidOperationException("Google generation requires a durable approved submission envelope.");

        authorization.EnsureStillAuthorized(
            envelope,
            authorization.Currency,
            authorization.Scale,
            currentEstimateMinorUnits);

        return new GoogleGenerationProductionRuntimeRequest(
            envelope.AccountId,
            envelope,
            envelope.PricingProvenanceId,
            envelope.RequestRevision,
            authorization.Currency,
            authorization.Scale,
            currentEstimateMinorUnits,
            snapshot).Validate();
    }
}
