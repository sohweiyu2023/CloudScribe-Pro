using System.Security.Cryptography;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Generation;

public sealed class GoogleGenerationBoundQueueCoordinator
{
    private readonly GoogleGenerationQueueCoordinator _queueCoordinator;
    private readonly Func<string, string, string, CancellationToken, Task<GoogleGenerationPersistedQueueState?>>? _loadPersistedState;
    private readonly Func<GoogleGenerationPersistedQueueState, CancellationToken, Task<bool>>? _tryClaimSubmission;
    private readonly Func<GoogleGenerationPersistedQueueState, CancellationToken, Task>? _saveSubmissionOutcome;

    public GoogleGenerationBoundQueueCoordinator(GoogleGenerationQueueCoordinator queueCoordinator)
        : this(queueCoordinator, null, null)
    {
    }

    public GoogleGenerationBoundQueueCoordinator(
        GoogleGenerationQueueCoordinator queueCoordinator,
        Func<string, string, string, CancellationToken, Task<GoogleGenerationPersistedQueueState?>>? loadPersistedState,
        Func<GoogleGenerationPersistedQueueState, CancellationToken, Task>? savePersistedState,
        Func<GoogleGenerationPersistedQueueState, CancellationToken, Task<bool>>? tryClaimSubmission = null,
        Func<GoogleGenerationPersistedQueueState, CancellationToken, Task>? saveSubmissionOutcome = null)
    {
        _queueCoordinator = queueCoordinator ?? throw new ArgumentNullException(nameof(queueCoordinator));
        if ((loadPersistedState is null) != (savePersistedState is null))
        {
            throw new ArgumentException(
                "Durable Google queue-state load and save delegates must be configured together.",
                nameof(loadPersistedState));
        }

        _loadPersistedState = loadPersistedState;
        _tryClaimSubmission = tryClaimSubmission;
        _saveSubmissionOutcome = saveSubmissionOutcome;
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
        var claimSubmission = _tryClaimSubmission
            ?? throw new InvalidOperationException("Atomic durable Google dispatch claims are not configured.");
        var saveOutcome = _saveSubmissionOutcome
            ?? throw new InvalidOperationException("Owned durable Google outcome persistence is not configured.");
        GoogleGenerationRequestBindingPolicy.RequireBound(request, admittedTrust);
        GoogleGenerationPersistedQueueState previous = await LoadCurrentStateAsync(request, cancellationToken).ConfigureAwait(false);
        GoogleGenerationPersistedQueueStatePolicy.RequireCompatible(previous, request.AccountId, request.OperationStableId, request.IdempotencyKey);
        var claim = previous with
        {
            SubmissionAttempted = true,
            UnresolvedSubmission = true,
            LocalAttemptId = Guid.NewGuid().ToString("N"),
            DiagnosticCode = "google-dispatch-claimed",
            PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(request.CompiledPayload.Span)),
            PayloadBytes = request.CompiledPayload.Length,
            OutcomeObservedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        bool ownsClaim = false;
        GoogleGenerationQueueOutcome outcome;
        try
        {
            outcome = await _queueCoordinator.ProcessClaimedAsync(request, admissionCurrent, accountCredentialAvailable,
                pricingApproved, postCompileLimitsSatisfied, false, async token =>
                {
                    if (!await claimSubmission(claim, token).ConfigureAwait(false))
                        throw new InvalidOperationException("This exact request was already claimed; no duplicate dispatch is permitted.");
                    ownsClaim = true;
                }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            if (ownsClaim)
            {
                // Caller cancellation cannot erase a committed pre-dispatch uncertainty barrier.
                using var bookkeeping = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await saveOutcome(claim with
                {
                    DiagnosticCode = "google-dispatch-outcome-unavailable",
                    OutcomeObservedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }, bookkeeping.Token).ConfigureAwait(false);
            }
            throw;
        }
        if (outcome.Response is null || !ownsClaim)
            return outcome;
        GoogleGenerationPersistedQueueState next = CreateNextPersistedState(claim, outcome.Response);
        GoogleGenerationPersistedQueueTransitionPolicy.ValidateTransition(claim, next,
            next.UnresolvedSubmission ? GoogleGenerationReconciliationResolutionEvidence.None
                : GoogleGenerationReconciliationResolutionEvidence.ProviderConfirmedTerminal);
        using var outcomeBookkeeping = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await saveOutcome(next, outcomeBookkeeping.Token).ConfigureAwait(false);
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
        bool unresolved = response.Disposition == SubmissionDisposition.UnknownRequiresReconciliation
            || !GoogleGenerationOutcomeDiagnostic.IsConfirmedTerminal(response.DiagnosticCode);
        return previous with
        {
            UnresolvedSubmission = unresolved,
            ProviderRequestId = providerRequestId,
            DiagnosticCode = GoogleGenerationOutcomeDiagnostic.Sanitize(response.DiagnosticCode),
            ErrorStatus = Enum.IsDefined(response.ErrorStatus) ? response.ErrorStatus : GoogleErrorStatus.Unknown,
            OutcomeObservedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }
}
