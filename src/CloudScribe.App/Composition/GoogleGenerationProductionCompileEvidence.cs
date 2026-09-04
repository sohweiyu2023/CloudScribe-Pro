using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed record GoogleGenerationProductionCompileEvidence
{
    public required SpeechPlan Plan { get; init; }

    public required GoogleSpeechCompilationOptions CompilationOptions { get; init; }

    public required GoogleGenerationAccount Account { get; init; }

    public required GoogleCapabilitySnapshot Capabilities { get; init; }

    public required string PricingProvenanceId { get; init; }

    public required int RequestRevision { get; init; }

    public required string ProjectId { get; init; }

    public required string ModelId { get; init; }

    public required string IdempotencyKey { get; init; }

    public required GenerationCacheTrustContext AdmittedTrust { get; init; }

    public required GoogleGenerationPersistedQueueState PreviousState { get; init; }

    public required GoogleGenerationPersistedQueueState CurrentState { get; init; }

    public required GoogleGenerationReconciliationResolutionEvidence ResolutionEvidence { get; init; }

    public required bool AccountAuthorized { get; init; }

    public required bool ProjectAuthorized { get; init; }

    public required bool CapabilityCurrent { get; init; }

    public required bool PricingCurrent { get; init; }

    public required bool AdmissionCurrent { get; init; }

    public required bool AccountCredentialAvailable { get; init; }

    public required bool PricingApproved { get; init; }

    public required bool PostCompileLimitsSatisfied { get; init; }

    public required string Currency { get; init; }

    public required int Scale { get; init; }

    public required long CurrentEstimateMinorUnits { get; init; }

    public required DateTimeOffset NowUtc { get; init; }
}
