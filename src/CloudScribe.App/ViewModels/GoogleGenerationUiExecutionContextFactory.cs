using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.App.ViewModels;

public static class GoogleGenerationUiExecutionContextFactory
{
    public static GoogleGenerationUiExecutionContext Create(
        GoogleGenerationAuthorizedRuntimeEvidence evidence,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateRequiredEvidence(evidence);

        TimeProvider clock = timeProvider ?? TimeProvider.System;
        DateTimeOffset nowUtc = clock.GetUtcNow();
        GoogleGenerationAccount account = evidence.Account.Validate();
        GoogleCapabilitySnapshot capabilities = ValidateCurrentCapabilities(evidence, account, nowUtc);
        GenerationProviderRequest request = ValidateCurrentRequest(evidence, account);
        RevalidateAuthorization(evidence, account, capabilities, request, nowUtc);

        return BuildContext(evidence, account, capabilities, clock);
    }

    private static void ValidateRequiredEvidence(GoogleGenerationAuthorizedRuntimeEvidence evidence)
    {
        if (evidence.Transport is null ||
            evidence.Account is null ||
            evidence.Capabilities is null ||
            evidence.SpendAuthorization is null ||
            evidence.Snapshot is null)
        {
            throw new ArgumentException("Complete Stage6 runtime evidence is required.", nameof(evidence));
        }

        if (string.IsNullOrWhiteSpace(evidence.PricingProvenanceId))
            throw new ArgumentException("Current pricing provenance is required.", nameof(evidence));
        if (string.IsNullOrWhiteSpace(evidence.Currency))
            throw new ArgumentException("Current provider-billed currency is required.", nameof(evidence));
        if (evidence.RequestRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(evidence), "Request revision cannot be negative.");
        if (evidence.Scale is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(evidence), "Currency scale must be between zero and nine.");
        if (evidence.CurrentEstimateMinorUnits < 0)
            throw new ArgumentOutOfRangeException(nameof(evidence), "Current estimate cannot be negative.");
    }

    private static GoogleCapabilitySnapshot ValidateCurrentCapabilities(
        GoogleGenerationAuthorizedRuntimeEvidence evidence,
        GoogleGenerationAccount account,
        DateTimeOffset nowUtc)
    {
        GoogleCapabilitySnapshot capabilities = evidence.Capabilities.Validate(nowUtc);
        if (capabilities.IsStale(nowUtc))
        {
            throw new InvalidOperationException(
                "Stale Google capability evidence cannot enter the production generation runtime.");
        }

        if (!string.Equals(account.AccountId, capabilities.AccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Google account and capability identities must match before production runtime composition.");
        }

        return capabilities;
    }

    private static GenerationProviderRequest ValidateCurrentRequest(
        GoogleGenerationAuthorizedRuntimeEvidence evidence,
        GoogleGenerationAccount account)
    {
        GenerationProviderRequest request = evidence.Snapshot.ProviderRequest
            ?? throw new InvalidOperationException("The Stage6 execution snapshot must contain a provider request.");
        if (!string.Equals(
                request.ProviderStableId,
                GoogleGenerationProvider.StableProviderId,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.OperationStableId,
                GoogleGenerationProvider.SynthesizeOperationStableId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Stage6 production runtime accepts only the exact Google synthesize-speech request namespace.");
        }

        if (!string.Equals(request.AccountId, account.AccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Stage6 provider request account differs from the current authorized Google account.");
        }

        return request;
    }

    private static void RevalidateAuthorization(
        GoogleGenerationAuthorizedRuntimeEvidence evidence,
        GoogleGenerationAccount account,
        GoogleCapabilitySnapshot capabilities,
        GenerationProviderRequest request,
        DateTimeOffset nowUtc)
    {
        GoogleGenerationSubmissionEnvelope envelope = evidence.SpendAuthorization.Envelope;
        envelope.EnsureStillAuthorized(
            account,
            capabilities,
            evidence.PricingProvenanceId,
            evidence.RequestRevision,
            request.CompiledPayload.Span,
            nowUtc);
        evidence.SpendAuthorization.EnsureStillAuthorized(
            envelope,
            evidence.Currency,
            evidence.Scale,
            evidence.CurrentEstimateMinorUnits);
    }

    private static GoogleGenerationUiExecutionContext BuildContext(
        GoogleGenerationAuthorizedRuntimeEvidence evidence,
        GoogleGenerationAccount account,
        GoogleCapabilitySnapshot capabilities,
        TimeProvider clock)
    {
        var provider = new GoogleGenerationProvider(account, evidence.Transport);
        var executor = new GoogleAuthorizedGenerationExecutor(
            provider,
            account,
            capabilities,
            evidence.SpendAuthorization,
            evidence.PricingProvenanceId,
            evidence.RequestRevision,
            evidence.Currency,
            evidence.Scale,
            evidence.CurrentEstimateMinorUnits,
            clock);
        var submission = new GoogleGenerationSubmissionCoordinator(executor);
        var queue = new GoogleGenerationQueueCoordinator(submission.SubmitAsync);
        var boundQueue = new GoogleGenerationBoundQueueCoordinator(queue);
        var uiQueue = new GoogleGenerationUiQueueCoordinator(boundQueue);

        return new GoogleGenerationUiExecutionContext(uiQueue, evidence.Snapshot);
    }
}
