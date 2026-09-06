using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Generation;

public sealed class GoogleGenerationBoundQueueCoordinator
{
    private readonly GoogleGenerationQueueCoordinator _queueCoordinator;
    private readonly Func<string, string, string, CancellationToken, Task<GoogleGenerationPersistedQueueState?>>? _loadPersistedState;
    private readonly Func<GoogleGenerationPersistedQueueState, CancellationToken, Task>? _savePersistedState;

    public GoogleGenerationBoundQueueCoordinator(GoogleGenerationQueueCoordinator queueCoordinator)
        : this(queueCoordinator, null, null)
    {
    }

    public GoogleGenerationBoundQueueCoordinator(
        GoogleGenerationQueueCoordinator queueCoordinator,
        Func<string, string, string, CancellationToken, Task<GoogleGenerationPersistedQueueState?>>? loadPersistedState,
        Func<GoogleGenerationPersistedQueueState, CancellationToken, Task>? savePersistedState)
    {
        _queueCoordinator = queueCoordinator ?? throw new ArgumentNullException(nameof(queueCoordinator));
        if ((loadPersistedState is null) != (savePersistedState is null))
        {
            throw new ArgumentException(
                "Durable Google queue-state load and save delegates must be configured together.",
                nameof(loadPersistedState));
        }

        _loadPersistedState = loadPersistedState;
        _savePersistedState = savePersistedState;
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

    public async Task<GoogleGenerationQueueOutcome> ProcessDurableAsync(
        GenerationProviderRequest request,
        GenerationCacheTrustContext admittedTrust,
        bool admissionCurrent,
        bool accountCredentialAvailable,
        bool pricingApproved,
        bool postCompileLimitsSatisfied,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admittedTrust);
        var save = _savePersistedState
            ?? throw new InvalidOperationException("Durable Google queue-state persistence is not configured.");

        GoogleGenerationPersistedQueueState previous = await LoadCurrentStateAsync(request, cancellationToken).ConfigureAwait(false);
        GoogleGenerationPersistedQueueStatePolicy.RequireCompatible(
            previous,
            request.AccountId,
            request.OperationStableId,
            request.IdempotencyKey);

        GoogleGenerationQueueOutcome outcome = await ProcessPersistedAsync(
            request,
            admittedTrust,
            previous,
            admissionCurrent,
            accountCredentialAvailable,
            pricingApproved,
            postCompileLimitsSatisfied,
            cancellationToken).ConfigureAwait(false);

        if (outcome.Response is null)
            return outcome;

        GoogleGenerationPersistedQueueState next = CreateNextPersistedState(previous, outcome.Response);
        GoogleGenerationPersistedQueueTransitionPolicy.ValidateTransition(previous, next);
        cancellationToken.ThrowIfCancellationRequested();
        await save(next, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    public Task<GoogleGenerationQueueOutcome> ProcessPersistedTransitionAsync(
        GenerationProviderRequest request,
        GenerationCacheTrustContext admittedTrust,
        GoogleGenerationPersistedQueueState previousState,
        GoogleGenerationPersistedQueueState currentState,
        bool admissionCurrent,
        bool accountCredentialAvailable,
        bool pricingApproved,
        bool postCompileLimitsSatisfied,
        CancellationToken cancellationToken = default) =>
        ProcessPersistedTransitionAsync(
            request,
            admittedTrust,
            previousState,
            currentState,
            GoogleGenerationReconciliationResolutionEvidence.None,
            admissionCurrent,
            accountCredentialAvailable,
            pricingApproved,
            postCompileLimitsSatisfied,
            cancellationToken);

    public Task<GoogleGenerationQueueOutcome> ProcessPersistedTransitionAsync(
        GenerationProviderRequest request,
        GenerationCacheTrustContext admittedTrust,
        GoogleGenerationPersistedQueueState previousState,
        GoogleGenerationPersistedQueueState currentState,
        GoogleGenerationReconciliationResolutionEvidence resolutionEvidence,
        bool admissionCurrent,
        bool accountCredentialAvailable,
        bool pricingApproved,
        bool postCompileLimitsSatisfied,
        CancellationToken cancellationToken = default)
    {
        var validated = GoogleGenerationPersistedQueueTransitionPolicy.ValidateTransition(
            previousState,
            currentState,
            resolutionEvidence);

        return ProcessPersistedAsync(
            request,
            admittedTrust,
            validated,
            admissionCurrent,
            accountCredentialAvailable,
            pricingApproved,
            postCompileLimitsSatisfied,
            cancellationToken);
    }

    private async Task<GoogleGenerationPersistedQueueState> LoadCurrentStateAsync(
        GenerationProviderRequest request,
        CancellationToken cancellationToken)
    {
        var load = _loadPersistedState
            ?? throw new InvalidOperationException("Durable Google queue-state loading is not configured.");
        cancellationToken.ThrowIfCancellationRequested();
        return await load(
            request.AccountId,
            request.OperationStableId,
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false)
            ?? new GoogleGenerationPersistedQueueState(
                request.AccountId,
                request.OperationStableId,
                request.IdempotencyKey,
                UnresolvedSubmission: false,
                ProviderRequestId: null).Validate();
    }

    private static GoogleGenerationPersistedQueueState CreateNextPersistedState(
        GoogleGenerationPersistedQueueState previous,
        GenerationProviderResponse response)
    {
        string? providerRequestId = string.IsNullOrWhiteSpace(response.ProviderRequestId)
            ? previous.ProviderRequestId
            : response.ProviderRequestId.Trim();
        bool unresolved = response.Disposition == SubmissionDisposition.UnknownRequiresReconciliation;
        if (unresolved && string.IsNullOrWhiteSpace(providerRequestId))
        {
            throw new InvalidOperationException(
                "An ambiguous Google provider outcome cannot be persisted without a genuine provider request identity.");
        }

        return new GoogleGenerationPersistedQueueState(
            previous.AccountId,
            previous.OperationStableId,
            previous.IdempotencyKey,
            unresolved,
            providerRequestId).Validate();
    }
}
