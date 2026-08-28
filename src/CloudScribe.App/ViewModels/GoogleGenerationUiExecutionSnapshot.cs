using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.App.ViewModels;

public sealed record GoogleGenerationUiExecutionSnapshot(
    GoogleGenerationUiSelection UiSelection,
    bool AccountAuthorized,
    bool ProjectAuthorized,
    bool CapabilityCurrent,
    bool PricingCurrent,
    GenerationProviderRequest ProviderRequest,
    GenerationCacheTrustContext AdmittedTrust,
    GoogleGenerationPersistedQueueState PreviousState,
    GoogleGenerationPersistedQueueState CurrentState,
    GoogleGenerationReconciliationResolutionEvidence ResolutionEvidence,
    bool AdmissionCurrent,
    bool AccountCredentialAvailable,
    bool PricingApproved,
    bool PostCompileLimitsSatisfied);
