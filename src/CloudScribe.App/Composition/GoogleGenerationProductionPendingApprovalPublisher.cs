using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// Publishes the exact current compiled Google submission into the explicit spend-approval boundary.
/// The caller must supply current persisted account/capability/pricing evidence and the matching
/// compiled UI execution snapshot; this component never reconstructs ambient or "latest" state.
/// </summary>
public sealed class GoogleGenerationProductionPendingApprovalPublisher(
    GoogleGenerationProductionPendingApprovalStateOwner pendingStateOwner)
{
    public GoogleGenerationProductionPendingApprovalStateOwner.PendingState Publish(
        GoogleGenerationAccount account,
        GoogleCapabilitySnapshot capabilities,
        string pricingProvenanceId,
        int requestRevision,
        GoogleGenerationUiExecutionSnapshot snapshot,
        string currency,
        int scale,
        long currentEstimateMinorUnits,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(pricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        GoogleGenerationProductionUiSnapshotValidator.Validate(snapshot);
        ReadOnlyMemory<byte> compiledPayload = snapshot.ProviderRequest.CompiledPayload;
        if (compiledPayload.IsEmpty)
        {
            throw new InvalidOperationException("Google generation pending approval requires the exact compiled provider payload.");
        }

        GoogleGenerationSubmissionEnvelope envelope = GoogleGenerationSubmissionEnvelope.Create(
            account,
            capabilities,
            pricingProvenanceId,
            requestRevision,
            snapshot.UiSelection.VoiceId,
            snapshot.ProviderRequest.OutputFormat,
            compiledPayload.Span,
            nowUtc);

        var pending = new GoogleGenerationProductionPendingApprovalStateOwner.PendingState(
            envelope,
            snapshot,
            currency,
            scale,
            currentEstimateMinorUnits);
        pendingStateOwner.Publish(pending);
        return pending;
    }
}
