using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.App.Composition;

/// <summary>
/// Performs the exact production Google compile and publishes only that compiled result into the
/// explicit spend-approval boundary. Authorization/currentness evidence is supplied by its real
/// upstream resolvers and is revalidated by the production snapshot and pending-state validators.
/// </summary>
public sealed class GoogleGenerationProductionCompileAndPrepareService(
    GoogleGenerationProductionPendingApprovalPublisher pendingApprovalPublisher)
{
    public GoogleGenerationProductionPendingApprovalStateOwner.PendingState CompileAndPrepare(
        GoogleGenerationProductionCompileEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateInputs(evidence);

        GoogleSpeechCompilation compilation = GoogleSpeechPlanCompiler.Compile(
            evidence.Plan,
            evidence.CompilationOptions);
        GenerationProviderRequest providerRequest = BuildProviderRequest(evidence, compilation);
        GoogleGenerationUiExecutionSnapshot snapshot = BuildSnapshot(evidence, providerRequest);

        return pendingApprovalPublisher.Publish(
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

    private static void ValidateInputs(GoogleGenerationProductionCompileEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence.Plan);
        ArgumentNullException.ThrowIfNull(evidence.CompilationOptions);
        ArgumentNullException.ThrowIfNull(evidence.Account);
        ArgumentNullException.ThrowIfNull(evidence.Capabilities);
        ArgumentNullException.ThrowIfNull(evidence.AdmittedTrust);
        ArgumentNullException.ThrowIfNull(evidence.PreviousState);
        ArgumentNullException.ThrowIfNull(evidence.CurrentState);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.PricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.IdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.Currency);

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
