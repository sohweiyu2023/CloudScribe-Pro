using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.App.Composition;

/// <summary>
/// Performs the exact production Google compile and publishes only that compiled result into the
/// explicit spend-approval boundary. Persisted account/capability evidence is re-read immediately
/// before compilation and must still match the request-bound evidence exactly.
/// </summary>
public sealed class GoogleGenerationProductionCompileAndPrepareService
{
    private readonly GoogleGenerationProductionPendingApprovalPublisher _pendingApprovalPublisher;
    private readonly GoogleGenerationProductionEvidenceResolver _productionEvidenceResolver;

    public GoogleGenerationProductionCompileAndPrepareService(
        GoogleGenerationProductionPendingApprovalPublisher pendingApprovalPublisher,
        GoogleGenerationProductionEvidenceResolver productionEvidenceResolver)
    {
        _pendingApprovalPublisher = pendingApprovalPublisher
            ?? throw new ArgumentNullException(nameof(pendingApprovalPublisher));
        _productionEvidenceResolver = productionEvidenceResolver
            ?? throw new ArgumentNullException(nameof(productionEvidenceResolver));
    }

    public async Task<GoogleGenerationProductionPendingApprovalStateOwner.PendingState> CompileAndPrepareAsync(
        GoogleGenerationProductionCompileEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateInputs(evidence);
        cancellationToken.ThrowIfCancellationRequested();

        GoogleGenerationProductionEvidence currentEvidence = await _productionEvidenceResolver.ResolveAsync(
            evidence.Account.AccountId,
            cancellationToken).ConfigureAwait(false);
        ValidateCurrentPersistedEvidence(evidence, currentEvidence);
        cancellationToken.ThrowIfCancellationRequested();

        GoogleSpeechCompilation compilation = GoogleSpeechPlanCompiler.Compile(
            evidence.Plan,
            evidence.CompilationOptions);
        GenerationProviderRequest providerRequest = BuildProviderRequest(evidence, compilation);
        GoogleGenerationUiExecutionSnapshot snapshot = BuildSnapshot(evidence, providerRequest);

        return _pendingApprovalPublisher.Publish(
            evidence.Account,
            evidence.Capabilities,
            evidence.PricingProvenanceId,
            evidence.RequestRevision,
            snapshot,
            evidence.Currency,
            evidence.Scale,
            evidence.CurrentEstimateMinorUnits,
            evidence.NowUtc);
    }

    private static void ValidateCurrentPersistedEvidence(
        GoogleGenerationProductionCompileEvidence evidence,
        GoogleGenerationProductionEvidence currentEvidence)
    {
        if (!currentEvidence.Account.IsEnabled)
        {
            throw new InvalidOperationException(
                "Current persisted Google account is disabled; generation cannot be compiled for approval.");
        }

        ProviderAccountReference persistedAccount = currentEvidence.Account.Reference;
        ProviderCapabilitySnapshot persistedCapability = currentEvidence.Capability.Snapshot;

        if (!string.Equals(persistedAccount.AccountId, evidence.Account.AccountId, StringComparison.Ordinal)
            || !string.Equals(
                persistedAccount.CredentialReference?.TargetName,
                evidence.Account.CredentialReferenceId,
                StringComparison.Ordinal)
            || persistedAccount.EndpointOrigin is null
            || Uri.Compare(
                persistedAccount.EndpointOrigin,
                evidence.Account.Endpoint,
                UriComponents.SchemeAndServer,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) != 0
            || !string.Equals(persistedAccount.RegionId, evidence.Account.Region, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Current persisted Google account evidence changed before compilation; refresh the generation request before approval.");
        }

        if (!string.Equals(persistedCapability.Account.AccountId, evidence.Capabilities.AccountId, StringComparison.Ordinal)
            || !string.Equals(persistedCapability.ProvenanceId, evidence.Capabilities.ProvenanceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Current persisted Google capability evidence changed before compilation; refresh the generation request before approval.");
        }
    }

    private static void ValidateInputs(GoogleGenerationProductionCompileEvidence evidence)
    {
        if (evidence.Plan is null)
        {
            throw new ArgumentException("Google generation compile evidence requires a speech plan.", nameof(evidence));
        }

        if (evidence.CompilationOptions is null)
        {
            throw new ArgumentException("Google generation compile evidence requires compilation options.", nameof(evidence));
        }

        if (evidence.Account is null)
        {
            throw new ArgumentException("Google generation compile evidence requires an account.", nameof(evidence));
        }

        if (evidence.Capabilities is null)
        {
            throw new ArgumentException("Google generation compile evidence requires capability evidence.", nameof(evidence));
        }

        if (evidence.AdmittedTrust is null || evidence.PreviousState is null || evidence.CurrentState is null)
        {
            throw new ArgumentException("Google generation compile evidence requires trust and queue state.", nameof(evidence));
        }

        if (string.IsNullOrWhiteSpace(evidence.PricingProvenanceId)
            || string.IsNullOrWhiteSpace(evidence.ProjectId)
            || string.IsNullOrWhiteSpace(evidence.ModelId)
            || string.IsNullOrWhiteSpace(evidence.IdempotencyKey)
            || string.IsNullOrWhiteSpace(evidence.Currency))
        {
            throw new ArgumentException("Google generation compile evidence contains a missing required identity.", nameof(evidence));
        }

        ValidatePrecompileAuthorizationState(evidence);
        evidence.Account.Validate();
        evidence.Capabilities.Validate(evidence.NowUtc).RequireSupported(
            evidence.CompilationOptions.VoiceName,
            evidence.CompilationOptions.AudioEncoding,
            evidence.CompilationOptions.MaximumPayloadBytes,
            evidence.NowUtc);
        evidence.AdmittedTrust.Validate();
        evidence.PreviousState.Validate();
        evidence.CurrentState.Validate();
    }

    private static void ValidatePrecompileAuthorizationState(GoogleGenerationProductionCompileEvidence evidence)
    {
        if (!evidence.AccountAuthorized || !evidence.ProjectAuthorized || !evidence.CapabilityCurrent ||
            !evidence.PricingCurrent || !evidence.AdmissionCurrent || !evidence.AccountCredentialAvailable ||
            !evidence.PricingApproved || !evidence.PostCompileLimitsSatisfied)
        {
            throw new InvalidOperationException(
                "Google generation cannot be compiled while authorization, pricing, admission, credential, capability, or limit evidence is not current.");
        }

        if (evidence.RequestRevision < 0)
        {
            throw new InvalidOperationException("Google generation request revision cannot be negative before compilation.");
        }

        if (evidence.Scale is < 0 or > 9)
        {
            throw new InvalidOperationException("Google generation currency scale must be between zero and nine before compilation.");
        }

        if (evidence.CurrentEstimateMinorUnits < 0)
        {
            throw new InvalidOperationException("Google generation current estimate cannot be negative before compilation.");
        }

        if (evidence.PreviousState.UnresolvedSubmission &&
            evidence.ResolutionEvidence == GoogleGenerationReconciliationResolutionEvidence.None)
        {
            throw new InvalidOperationException(
                "An unresolved persisted Google submission requires reconciliation evidence before compilation.");
        }
    }

    private static GenerationProviderRequest BuildProviderRequest(
        GoogleGenerationProductionCompileEvidence evidence,
        GoogleSpeechCompilation compilation) =>
        new(
            GoogleGenerationProvider.StableProviderId,
            GoogleGenerationProvider.SynthesizeOperationStableId,
            evidence.Account.AccountId,
            evidence.IdempotencyKey,
            compilation.Payload,
            evidence.CompilationOptions.AudioEncoding);

    private static GoogleGenerationUiExecutionSnapshot BuildSnapshot(
        GoogleGenerationProductionCompileEvidence evidence,
        GenerationProviderRequest providerRequest) =>
        new(
            new GoogleGenerationUiSelection(
                evidence.Account.AccountId,
                evidence.ProjectId,
                evidence.CompilationOptions.VoiceName,
                evidence.ModelId,
                evidence.Capabilities.ProvenanceId,
                evidence.CompilationOptions.AudioEncoding),
            evidence.AccountAuthorized,
            evidence.ProjectAuthorized,
            evidence.CapabilityCurrent,
            evidence.PricingCurrent,
            providerRequest,
            evidence.AdmittedTrust,
            evidence.PreviousState,
            evidence.CurrentState,
            evidence.ResolutionEvidence,
            evidence.AdmissionCurrent,
            evidence.AccountCredentialAvailable,
            evidence.PricingApproved,
            evidence.PostCompileLimitsSatisfied);
}
