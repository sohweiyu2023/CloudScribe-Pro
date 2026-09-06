using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Generation;

public sealed class GoogleGenerationUiQueueCoordinator
{
    private const string GoogleProviderStableId = "google-cloud-text-to-speech";
    private const string GoogleOperationStableId = "synthesize-speech";
    private readonly GoogleGenerationBoundQueueCoordinator _boundQueue;
    private int _queueInFlight;

    public GoogleGenerationUiQueueCoordinator(GoogleGenerationBoundQueueCoordinator boundQueue)
    {
        _boundQueue = boundQueue ?? throw new ArgumentNullException(nameof(boundQueue));
    }

    public async Task<GoogleGenerationQueueOutcome> ProcessPersistedTransitionAsync(
        GoogleGenerationUiSelection uiSelection,
        bool accountAuthorized,
        bool projectAuthorized,
        bool capabilityCurrent,
        bool pricingCurrent,
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
        if (Interlocked.CompareExchange(ref _queueInFlight, 1, 0) != 0)
            throw new InvalidOperationException("A Google UI generation request is already in progress at this coordinator boundary.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentSelection = GoogleGenerationUiAdmission.RequireCurrent(
                uiSelection,
                accountAuthorized,
                projectAuthorized,
                capabilityCurrent,
                pricingCurrent);

            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(admittedTrust);
            ArgumentNullException.ThrowIfNull(previousState);
            ArgumentNullException.ThrowIfNull(currentState);

            if (!string.Equals(admittedTrust.ProviderStableId, GoogleProviderStableId, StringComparison.Ordinal) ||
                !string.Equals(admittedTrust.OperationStableId, GoogleOperationStableId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Google UI queue admission requires the exact Google synthesize-speech trust namespace.");
            }

            GoogleGenerationUiTrustBindingPolicy.RequireExactBinding(currentSelection, admittedTrust);

            if (!string.Equals(currentSelection.AccountId, request.AccountId, StringComparison.Ordinal))
                throw new InvalidOperationException("Google UI account identity differs from the bound provider request.");
            if (!string.Equals(currentSelection.OutputFormat, request.OutputFormat, StringComparison.Ordinal))
                throw new InvalidOperationException("Google UI output format differs from the bound provider request.");

            // The authorization-time transition is still validated as evidence, but it is never
            // treated as the production queue state. The bound coordinator reloads the durable
            // state immediately before submission and persists only a state derived from the
            // genuine provider outcome.
            GoogleGenerationPersistedQueueTransitionPolicy.ValidateTransition(
                previousState,
                currentState,
                resolutionEvidence);

            cancellationToken.ThrowIfCancellationRequested();
            return await _boundQueue.ProcessDurableAsync(
                request,
                admittedTrust,
                admissionCurrent,
                accountCredentialAvailable,
                pricingApproved,
                postCompileLimitsSatisfied,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _queueInFlight, 0);
        }
    }
}
