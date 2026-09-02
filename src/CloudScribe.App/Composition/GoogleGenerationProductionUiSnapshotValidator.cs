using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// Revalidates the cross-layer identities in a Stage6 UI execution snapshot before it may
/// enter production runtime composition. This validator deliberately does not manufacture
/// authorization/currentness flags; callers must supply them from their real evidence sources.
/// </summary>
public static class GoogleGenerationProductionUiSnapshotValidator
{
    public static GoogleGenerationUiExecutionSnapshot Validate(GoogleGenerationUiExecutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.UiSelection is null || snapshot.ProviderRequest is null || snapshot.AdmittedTrust is null ||
            snapshot.PreviousState is null || snapshot.CurrentState is null)
        {
            throw new InvalidOperationException("Complete persisted/current Stage6 UI evidence is required.");
        }

        snapshot.AdmittedTrust.Validate();
        snapshot.PreviousState.Validate();
        snapshot.CurrentState.Validate();

        RequireEqual(snapshot.UiSelection.AccountId, snapshot.ProviderRequest.AccountId,
            "Stage6 UI account differs from the provider request account.");
        RequireEqual(snapshot.UiSelection.AccountId, snapshot.AdmittedTrust.AccountId,
            "Stage6 UI account differs from admitted trust.");
        RequireEqual(snapshot.UiSelection.ProjectId, snapshot.AdmittedTrust.ProjectId,
            "Stage6 UI project differs from admitted trust.");
        RequireEqual(snapshot.UiSelection.VoiceId, snapshot.AdmittedTrust.VoiceStableId,
            "Stage6 UI voice differs from admitted trust.");
        RequireEqual(snapshot.UiSelection.ModelId, snapshot.AdmittedTrust.ResolvedModelId,
            "Stage6 UI model differs from admitted trust.");
        RequireEqual(snapshot.UiSelection.CapabilityEvidenceId, snapshot.AdmittedTrust.CapabilityIdentity,
            "Stage6 capability evidence differs from admitted trust.");
        RequireEqual(snapshot.UiSelection.OutputFormat, snapshot.ProviderRequest.OutputFormat,
            "Stage6 UI output format differs from the provider request.");
        RequireEqual(snapshot.UiSelection.OutputFormat, snapshot.AdmittedTrust.OutputFormat,
            "Stage6 UI output format differs from admitted trust.");

        RequireEqual(GoogleGenerationProvider.StableProviderId, snapshot.ProviderRequest.ProviderStableId,
            "Stage6 production snapshot is not bound to the Google provider.");
        RequireEqual(GoogleGenerationProvider.StableProviderId, snapshot.AdmittedTrust.ProviderStableId,
            "Stage6 admitted trust is not bound to the Google provider.");
        RequireEqual(GoogleGenerationProvider.SynthesizeOperationStableId, snapshot.ProviderRequest.OperationStableId,
            "Stage6 production snapshot is not bound to Google synthesis.");
        RequireEqual(snapshot.ProviderRequest.OperationStableId, snapshot.AdmittedTrust.OperationStableId,
            "Stage6 provider operation differs from admitted trust.");

        ValidateQueueState(snapshot.PreviousState, snapshot.ProviderRequest);
        ValidateQueueState(snapshot.CurrentState, snapshot.ProviderRequest);

        if (snapshot.PreviousState.UnresolvedSubmission &&
            snapshot.ResolutionEvidence == Application.Generation.GoogleGenerationReconciliationResolutionEvidence.None)
        {
            throw new InvalidOperationException("An unresolved persisted Google submission requires genuine reconciliation evidence before retry.");
        }

        if (!snapshot.AccountAuthorized || !snapshot.ProjectAuthorized || !snapshot.CapabilityCurrent ||
            !snapshot.PricingCurrent || !snapshot.AdmissionCurrent || !snapshot.AccountCredentialAvailable ||
            !snapshot.PricingApproved || !snapshot.PostCompileLimitsSatisfied)
        {
            throw new InvalidOperationException("Stage6 production UI snapshot contains non-current or non-authorized evidence.");
        }

        return snapshot;
    }

    private static void ValidateQueueState(
        Application.Generation.GoogleGenerationPersistedQueueState state,
        Providers.Abstractions.GenerationProviderRequest request)
    {
        RequireEqual(state.AccountId, request.AccountId,
            "Persisted Google queue account differs from the provider request.");
        RequireEqual(state.OperationStableId, request.OperationStableId,
            "Persisted Google queue operation differs from the provider request.");
        RequireEqual(state.IdempotencyKey, request.IdempotencyKey,
            "Persisted Google queue idempotency identity differs from the provider request.");
    }

    private static void RequireEqual(string expected, string actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(message);
        }
    }
}
