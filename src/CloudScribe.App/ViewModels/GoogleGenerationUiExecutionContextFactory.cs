using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.ViewModels;

public sealed record GoogleGenerationAuthorizedRuntimeEvidence(
    GoogleGenerationHttpTransport Transport,
    GoogleGenerationAccount Account,
    GoogleCapabilitySnapshot Capabilities,
    GoogleGenerationSpendAuthorization SpendAuthorization,
    string PricingProvenanceId,
    int RequestRevision,
    string Currency,
    int Scale,
    long CurrentEstimateMinorUnits,
    GoogleGenerationUiExecutionSnapshot Snapshot);

public static class GoogleGenerationUiExecutionContextFactory
{
    public static GoogleGenerationUiExecutionContext Create(
        GoogleGenerationAuthorizedRuntimeEvidence evidence,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.Transport);
        ArgumentNullException.ThrowIfNull(evidence.Account);
        ArgumentNullException.ThrowIfNull(evidence.Capabilities);
        ArgumentNullException.ThrowIfNull(evidence.SpendAuthorization);
        ArgumentNullException.ThrowIfNull(evidence.Snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.PricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.Currency);
        ArgumentOutOfRangeException.ThrowIfNegative(evidence.RequestRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(evidence.Scale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(evidence.Scale, 9);
        ArgumentOutOfRangeException.ThrowIfNegative(evidence.CurrentEstimateMinorUnits);

        TimeProvider clock = timeProvider ?? TimeProvider.System;
        DateTimeOffset nowUtc = clock.GetUtcNow();
        GoogleGenerationAccount account = evidence.Account.Validate();
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

        var request = evidence.Snapshot.ProviderRequest
            ?? throw new InvalidOperationException("The Stage6 execution snapshot must contain a provider request.");
        if (!string.Equals(
                request.ProviderStableId,
                GoogleGenerationProvider.StableProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
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
