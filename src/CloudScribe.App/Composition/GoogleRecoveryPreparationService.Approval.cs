using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleRecoveryPreparationService
{
    public async Task ApproveAsync(ShellViewModel.GoogleTtsStudioRequestSelection selection,
        GoogleRecoveryApprovalConfirmation confirmation, CancellationToken cancellationToken)
    {
        if (!confirmation.ConfirmedByUser || !confirmation.AcknowledgedHistoricalHolds
            || !_pending.TryGetValue(confirmation.PreviewId, out var prepared)
            || prepared.DocumentId != selection.DocumentId || prepared.RevisionId != selection.RevisionId
            || _clock.GetUtcNow() - prepared.CreatedAt > TimeSpan.FromMinutes(10)
            || !string.Equals(prepared.Preview.ReviewSha256, confirmation.DisplayedReviewSha256, StringComparison.Ordinal)
            || prepared.Preview.AdditionalMaximumMinorUnits != confirmation.AdditionalMaximumMinorUnits)
            throw new InvalidOperationException("A current exact recovery review and explicit historical hold acknowledgement are required.");
        var pending = prepared.Pending;
        var current = await _currentCompilation.CompileReadonlyAsync(selection, cancellationToken).ConfigureAwait(false);
        if (current.Envelope != pending.Envelope || !Equals(current.Snapshot.UiSelection, pending.Snapshot.UiSelection)
            || !string.Equals(ArticleAudioManifest.HashText(selection.ExactText),
                ArticleAudioManifest.HashText(ReadPlainText(pending.Snapshot.ProviderRequest.CompiledPayload)), StringComparison.Ordinal))
            throw new InvalidOperationException("The selected narration changed after recovery review.");
        if (prepared.Preview.ExistingApproval is not null)
            await RequireCurrentResumeReviewAsync(prepared, current, selection, confirmation, cancellationToken).ConfigureAwait(false);
        // Lost acknowledgement may resume the SAME committed approval, never allocate a second child.
        var approved = prepared.Preview.ExistingApproval
            ?? await _recovery.FindApprovedAsync(confirmation.DisplayedReviewSha256, cancellationToken).ConfigureAwait(false)
            ?? await _recovery.ApproveAsync(confirmation, cancellationToken).ConfigureAwait(false);
        if (approved.ReservedMinorUnits != confirmation.AdditionalMaximumMinorUnits)
            throw new InvalidOperationException("The exact displayed recovery reservation changed.");
        var envelope = pending.Envelope with { RecoveryAuthorizationId = approved.AuthorizationId };
        var request = pending.Snapshot.ProviderRequest;
        var childRequest = new GenerationProviderRequest(request.ProviderStableId, request.OperationStableId, request.AccountId,
            approved.IdempotencyKey, request.CompiledPayload.ToArray(), request.OutputFormat);
        var clean = new GoogleGenerationPersistedQueueState(request.AccountId, request.OperationStableId, approved.IdempotencyKey, false, null);
        var snapshot = pending.Snapshot with { ProviderRequest = childRequest, PreviousState = clean, CurrentState = clean, PricingApproved = true };
        await _state.ActivateRecoveryAsync(envelope, snapshot, pending.CurrentEstimateMinorUnits, cancellationToken).ConfigureAwait(false);
        _pending.TryRemove(confirmation.PreviewId, out _);
    }

    private async Task RequireCurrentResumeReviewAsync(PreparedRecovery prepared,
        GoogleGenerationProductionPendingApprovalStateOwner.PendingState current, ShellViewModel.GoogleTtsStudioRequestSelection selection,
        GoogleRecoveryApprovalConfirmation confirmation, CancellationToken cancellationToken)
    {
        var policy = await _admission.LoadVerifiedPolicyAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Resume authority is no longer admitted.");
        var roots = policy.Predecessors.Where(x => string.Equals(x.HistoricalKey, prepared.Preview.AssociationProof.RootHistoricalKey, StringComparison.Ordinal)
            && x.DocumentId == selection.DocumentId && x.RevisionId == selection.RevisionId).ToArray();
        if (roots.Length != 1) throw new InvalidOperationException("Current admitted resume association changed.");
        var existing = await _recovery.LoadPendingForCurrentEnvelopeAsync(current.Envelope, selection.ProjectStableId,
            selection.DocumentId, prepared.RevisionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The exact pending reservation is unavailable.");
        var fresh = CreateResumeReview(prepared.Preview.Intent, roots[0], existing, confirmation.AdditionalMaximumMinorUnits, policy);
        if (fresh.ExistingApproval != prepared.Preview.ExistingApproval
            || !string.Equals(fresh.ReviewSha256, confirmation.DisplayedReviewSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Resume budget, financial state or admitted authority changed. Prepare a fresh review of the same retained reservation.");
    }

    public void Discard(Guid previewId)
    {
        _pending.TryRemove(previewId, out _);
        _recovery.DiscardUnapprovedPreview(previewId);
    }

    private static string ReadPlainText(ReadOnlyMemory<byte> payload)
    {
        using var document = System.Text.Json.JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("input").GetProperty("text").GetString()
            ?? throw new InvalidOperationException("Recovery requires exact plain text.");
    }
}
