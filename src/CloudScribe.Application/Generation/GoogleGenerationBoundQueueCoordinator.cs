using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Generation;

public sealed class GoogleGenerationBoundQueueCoordinator
{
    private readonly GoogleGenerationQueueCoordinator _queueCoordinator;

    public GoogleGenerationBoundQueueCoordinator(GoogleGenerationQueueCoordinator queueCoordinator)
    {
        _queueCoordinator = queueCoordinator ?? throw new ArgumentNullException(nameof(queueCoordinator));
    }

    public Task<GoogleGenerationQueueOutcome> ProcessAsync(
        GenerationProviderRequest request,
        GenerationCacheTrustContext admittedTrust,
        bool admissionCurrent,
        bool accountCredentialAvailable,
        bool pricingApproved,
        bool postCompileLimitsSatisfied,
        bool unresolvedPriorSubmission,
        string? persistedIdempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admittedTrust);

        GoogleGenerationRequestBindingPolicy.RequireBound(request, admittedTrust);
        if (!admissionCurrent)
            throw new InvalidOperationException("Google queue execution requires the same current v2.23 admission used to bind the provider request.");

        GoogleGenerationReconciliationBarrier.RequireNoDuplicateSubmission(
            unresolvedPriorSubmission,
            request.IdempotencyKey,
            persistedIdempotencyKey);

        return _queueCoordinator.ProcessAsync(
            request,
            admissionCurrent,
            accountCredentialAvailable,
            pricingApproved,
            postCompileLimitsSatisfied,
            unresolvedPriorSubmission,
            cancellationToken);
    }

    public Task<GoogleGenerationQueueOutcome> ProcessPersistedAsync(
        GenerationProviderRequest request,
        GenerationCacheTrustContext admittedTrust,
        GoogleGenerationPersistedQueueState persistedState,
        bool admissionCurrent,
        bool accountCredentialAvailable,
        bool pricingApproved,
        bool postCompileLimitsSatisfied,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admittedTrust);
        ArgumentNullException.ThrowIfNull(persistedState);

        GoogleGenerationRequestBindingPolicy.RequireBound(request, admittedTrust);
        GoogleGenerationPersistedQueueStatePolicy.RequireCompatible(
            persistedState,
            request.AccountId,
            request.OperationStableId,
            request.IdempotencyKey);

        return ProcessAsync(
            request,
            admittedTrust,
            admissionCurrent,
            accountCredentialAvailable,
            pricingApproved,
            postCompileLimitsSatisfied,
            persistedState.UnresolvedSubmission,
            persistedState.IdempotencyKey,
            cancellationToken);
    }
}
