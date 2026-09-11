using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// Establishes the exact persisted queue state for a Studio request before Stage6 compilation.
/// A genuinely new idempotency identity is persisted as a clean, never-submitted state. Existing
/// unresolved state is never cleared or reconciled here; it fails closed and must follow the real
/// reconciliation workflow.
/// </summary>
internal sealed class GoogleTtsStudioQueueStateResolver(
    IGoogleGenerationPersistedQueueStateStore queueStateStore)
{
    private readonly IGoogleGenerationPersistedQueueStateStore _queueStateStore =
        queueStateStore ?? throw new ArgumentNullException(nameof(queueStateStore));

    public async Task<QueueCapture> ResolveAsync(
        GoogleGenerationProductionRequestIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        intent.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        GoogleGenerationPersistedQueueState? persisted = await _queueStateStore
            .LoadAsync(
                intent.AccountId,
                GoogleGenerationProvider.SynthesizeOperationStableId,
                intent.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);

        if (persisted is null)
        {
            var initial = new GoogleGenerationPersistedQueueState(
                intent.AccountId,
                GoogleGenerationProvider.SynthesizeOperationStableId,
                intent.IdempotencyKey,
                UnresolvedSubmission: false,
                ProviderRequestId: null).Validate();
            await _queueStateStore.SaveAsync(initial, cancellationToken).ConfigureAwait(false);
            persisted = await _queueStateStore
                .LoadAsync(
                    intent.AccountId,
                    GoogleGenerationProvider.SynthesizeOperationStableId,
                    intent.IdempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Google generation queue initialization committed without readable persisted state.");
            if (!Equals(initial, persisted))
            {
                throw new InvalidOperationException(
                    "Persisted Google generation queue state changed while initializing the exact Studio request.");
            }
        }

        persisted.Validate();
        if (persisted.UnresolvedSubmission)
        {
            throw new InvalidOperationException(
                "This exact Google generation request has an unresolved prior submission. Reconcile the persisted provider request before preparing new spend approval.");
        }

        // No state transition is invented during precompile capture. For a clean request the
        // authoritative persisted state is both the previous and current state; the transition
        // policy verifies that no reconciliation evidence is required.
        GoogleGenerationPersistedQueueTransitionPolicy.ValidateTransition(
            persisted,
            persisted,
            GoogleGenerationReconciliationResolutionEvidence.None);
        return new QueueCapture(
            persisted,
            persisted,
            GoogleGenerationReconciliationResolutionEvidence.None);
    }

    internal sealed record QueueCapture(
        GoogleGenerationPersistedQueueState PreviousState,
        GoogleGenerationPersistedQueueState CurrentState,
        GoogleGenerationReconciliationResolutionEvidence ResolutionEvidence);
}
