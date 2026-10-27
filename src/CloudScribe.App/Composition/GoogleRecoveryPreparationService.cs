using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>Read-only current compilation plus explicit durable approval. No provider dispatch dependency.</summary>
public sealed partial class GoogleRecoveryPreparationService
{
    private readonly InstalledRecoveryAdmissionStore _admission;
    private readonly GoogleExplicitRecoveryService _recovery;
    private readonly GoogleGenerationProductionSubmissionStateOwner _state;
    private readonly IGoogleRecoveryCurrentCompilation _currentCompilation;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<Guid, PreparedRecovery> _pending = new();

    public GoogleRecoveryPreparationService(InstalledRecoveryAdmissionStore admission,
        IGoogleRecoveryCurrentCompilation currentCompilation, GoogleExplicitRecoveryService recovery,
        GoogleGenerationProductionSubmissionStateOwner state, TimeProvider clock)
    { _admission = admission; _currentCompilation = currentCompilation; _recovery = recovery; _state = state; _clock = clock; }

    public async Task<GoogleRecoveryPreview> PrepareAsync(ShellViewModel.GoogleTtsStudioRequestSelection selection,
        string historicalKey, long additionalMaximumMinorUnits, CancellationToken cancellationToken)
    {
        var policy = await _admission.LoadVerifiedPolicyAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No current recovery policy is admitted.");
        var anchors = policy.Predecessors.Where(x => x.DocumentId == selection.DocumentId && x.RevisionId == selection.RevisionId
            && string.Equals(x.AccountId, selection.AccountStableId, StringComparison.Ordinal)
            && string.Equals(x.ProjectId, selection.ProjectStableId, StringComparison.Ordinal)
            && string.Equals(x.TextSha256, ArticleAudioManifest.HashText(selection.ExactText), StringComparison.Ordinal)).ToArray();
        if (anchors.Length != 1) throw new InvalidOperationException("Current selection does not match independently admitted historical narration.");
        if (!string.Equals(await _recovery.FindLatestKeyAsync(anchors[0], cancellationToken).ConfigureAwait(false), historicalKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery must extend the exact latest historical attempt.");
        var pending = await _currentCompilation.CompileReadonlyAsync(selection, cancellationToken).ConfigureAwait(false);
        var intent = new GoogleRecoveryIntent(selection.DocumentId, anchors[0].RevisionId, selection.ProjectStableId,
            historicalKey, pending.Envelope, pending.Currency, pending.Scale, pending.CurrentEstimateMinorUnits,
            pending.Snapshot.ProviderRequest.CompiledPayload);
        var existing = await _recovery.LoadPendingForCurrentEnvelopeAsync(pending.Envelope, selection.ProjectStableId,
            selection.DocumentId, anchors[0].RevisionId, cancellationToken).ConfigureAwait(false);
        var preview = existing is null ? await _recovery.PrepareAsync(intent, additionalMaximumMinorUnits, cancellationToken).ConfigureAwait(false)
            : CreateResumeReview(intent, anchors[0], existing, additionalMaximumMinorUnits, policy);
        foreach (var entry in _pending)
            if (_clock.GetUtcNow() - entry.Value.CreatedAt > TimeSpan.FromMinutes(10)) Discard(entry.Key);
        if (_pending.Count >= 32)
        { _recovery.DiscardUnapprovedPreview(preview.PreviewId); throw new InvalidOperationException("Close existing recovery reviews before preparing another."); }
        _pending[preview.PreviewId] = new(pending, preview, selection.DocumentId, anchors[0].RevisionId, _clock.GetUtcNow());
        return preview;
    }

    private static GoogleRecoveryPreview CreateResumeReview(GoogleRecoveryIntent intent, GoogleRecoveryPredecessorEvidence root,
        GoogleRecoveryExecutionAuthorization existing, long maximum, RecoveryAdmissionPolicy policy)
    {
        if (maximum != existing.Approval.ReservedMinorUnits || existing.Budget is null)
            throw new InvalidOperationException("Resume requires review of the exact existing reserved maximum; it cannot reserve another amount.");
        var proof = new GoogleRecoveryAssociationProof(root.LogicalNarrationId, root.HistoricalKey, root.HistoricalStateSha256, root.AdmissionSha256);
        string digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Kind = "resume-existing-reservation-v1",
            existing.Approval,
            Intent = intent,
            Proof = proof,
            existing.Budget,
            existing.FinancialStateSha256,
            PolicySha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(policy)))
        })));
        return new(Guid.NewGuid(), digest, root.LogicalNarrationId, intent,
            new(intent.Envelope.AccountId, GoogleGenerationProvider.SynthesizeOperationStableId, existing.Approval.IdempotencyKey, false, null),
            existing.Budget, maximum, existing.FinancialStateSha256,
            proof, existing.Approval);
    }

    public async Task<IReadOnlyList<string>> ListCurrentHistoricalKeysAsync(ShellViewModel.GoogleTtsStudioRequestSelection selection,
        CancellationToken cancellationToken)
    {
        var policy = await _admission.LoadVerifiedPolicyAsync(cancellationToken).ConfigureAwait(false);
        if (policy is null) return [];
        List<string> keys = [];
        foreach (var root in policy.Predecessors.Where(x => x.DocumentId == selection.DocumentId && x.RevisionId == selection.RevisionId
            && string.Equals(x.AccountId, selection.AccountStableId, StringComparison.Ordinal)
            && string.Equals(x.ProjectId, selection.ProjectStableId, StringComparison.Ordinal)
            && string.Equals(x.TextSha256, ArticleAudioManifest.HashText(selection.ExactText), StringComparison.Ordinal)))
            keys.Add(await _recovery.FindLatestKeyAsync(root, cancellationToken).ConfigureAwait(false));
        return keys;
    }

    private sealed record PreparedRecovery(GoogleGenerationProductionPendingApprovalStateOwner.PendingState Pending,
        GoogleRecoveryPreview Preview, Guid DocumentId, Guid RevisionId, DateTimeOffset CreatedAt);
}
