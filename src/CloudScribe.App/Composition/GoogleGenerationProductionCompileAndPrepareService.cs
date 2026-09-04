using System.Security.Cryptography;
using CloudScribe.Application.Generation;
using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.App.Composition;

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

        GoogleSpeechCompilation compilation = GoogleSpeechPlanCompiler.Compile(evidence.Plan, evidence.CompilationOptions);
        GenerationProviderRequest providerRequest = BuildProviderRequest(evidence, compilation);
        GoogleGenerationUiExecutionSnapshot snapshot = BuildSnapshot(evidence, providerRequest);

        return _pendingApprovalPublisher.Publish(
            evidence.Account,
            evidence.Capabilities,
            providerRequest,
            snapshot,
            evidence.PricingProvenanceId,
            evidence.RequestRevision,
            evidence.ProjectId,
            evidence.ModelId,
            evidence.IdempotencyKey,
            evidence.AdmittedTrust,
            evidence.PreviousState,
            evidence.CurrentState,
            evidence.ResolutionEvidence,
            evidence.AccountAuthorized,
            evidence.ProjectAuthorized,
            evidence.CapabilityCurrent,
            evidence.PricingCurrent,
            evidence.AdmissionCurrent,
            evidence.AccountCredentialAvailable,
            evidence.PricingApproved,
            evidence.PostCompileLimitsSatisfied,
            evidence.Currency,
            evidence.Scale,
            evidence.CurrentEstimateMinorUnits,
            evidence.NowUtc);
    }

    private static void ValidateCurrentPersistedEvidence(
        GoogleGenerationProductionCompileEvidence evidence,
        GoogleGenerationProductionEvidence currentEvidence)
    {
        ProviderAccountReference persistedAccount = currentEvidence.Account.Reference;
        ProviderCapabilitySnapshot persistedCapability = currentEvidence.Capability.Snapshot;

        if (!string.Equals(persistedAccount.AccountId, evidence.Account.AccountId, StringComparison.Ordinal) ||
            !string.Equals(
                persistedAccount.CredentialReference?.TargetName,
                evidence.Account.CredentialReferenceId,
                StringComparison.Ordinal) ||
            persistedAccount.EndpointOrigin is null ||
            !Uri.Compare(
                    persistedAccount.EndpointOrigin,
                    evidence.Account.Endpoint,
                    UriComponents.SchemeAndServer,
                    UriFormat.SafeUnescaped,
                    StringComparison.OrdinalIgnoreCase)
                .Equals(0) ||
            !string.Equals(persistedAccount.RegionId, evidence.Account.Region, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Current persisted Google account evidence changed before compilation; refresh the generation request before approval.");
        }

        if (!string.Equals(persistedCapability.Account.AccountId, evidence.Capabilities.AccountId, StringComparison.Ordinal) ||
            !string.Equals(persistedCapability.ProvenanceId, evidence.Capabilities.ProvenanceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Current persisted Google capability evidence changed before compilation; refresh the generation request before approval.");
        }
    }

    private static GenerationProviderRequest BuildProviderRequest(
        GoogleGenerationProductionCompileEvidence evidence,
        GoogleSpeechCompilation compilation)
    {
        string operationId = $"google-generation:{evidence.RequestRevision}:{evidence.IdempotencyKey}";
        return new GenerationProviderRequest(
            operationId,
            GoogleGenerationProvider.SynthesizeOperationStableId,
            evidence.Plan.Voice.VoiceStableId,
            compilation.ContentType,
            compilation.Payload,
            evidence.IdempotencyKey,
            evidence.ProjectId,
            evidence.ModelId,
            evidence.PricingProvenanceId);
    }

    private static GoogleGenerationUiExecutionSnapshot BuildSnapshot(
        GoogleGenerationProductionCompileEvidence evidence,
        GenerationProviderRequest providerRequest)
    {
        byte[] hash = SHA256.HashData(providerRequest.Payload.Span);
        return new GoogleGenerationUiExecutionSnapshot(
            evidence.Account.AccountId,
            evidence.Capabilities.ProvenanceId,
            evidence.PricingProvenanceId,
            evidence.Plan.Voice.VoiceStableId,
            evidence.CompilationOptions.AudioEncoding,
            providerRequest.Payload.Length,
            Convert.ToHexString(hash));
    }

    private static void ValidateInputs(GoogleGenerationProductionCompileEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence.Plan);
        ArgumentNullException.ThrowIfNull(evidence.CompilationOptions);
        ArgumentNullException.ThrowIfNull(evidence.Account);
        ArgumentNullException.ThrowIfNull(evidence.Capabilities);
        ArgumentNullException.ThrowIfNull(evidence.AdmittedTrust);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.PricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.IdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.Currency);
        if (evidence.RequestRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(evidence), "Request revision cannot be negative.");
        if (evidence.CurrentEstimateMinorUnits < 0)
            throw new ArgumentOutOfRangeException(nameof(evidence), "Current estimate cannot be negative.");
        if (evidence.Scale < 0 || evidence.Scale > 9)
            throw new ArgumentOutOfRangeException(nameof(evidence), "Currency scale must be between zero and nine.");
        if (evidence.NowUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Compile evidence timestamps must be UTC.", nameof(evidence));

        evidence.Account.Validate();
        evidence.Capabilities.Validate(evidence.NowUtc);
        evidence.AdmittedTrust.Validate();
        evidence.PreviousState.Validate();
        evidence.CurrentState.Validate();
    }
}
