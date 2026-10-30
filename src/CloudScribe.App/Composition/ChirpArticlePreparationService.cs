using System.Collections.Concurrent;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.App.Composition;

/// <summary>Compiles one independently resolved native source slice through the existing production compiler, then activates dedicated durable approval.</summary>
public sealed partial class ChirpArticlePreparationService(ChirpArticleService articles, IGoogleRecoveryCurrentCompilation compilation,
    GoogleGenerationProductionSubmissionStateOwner state, TimeProvider clock) : IGoogleRecoveryCurrentCompilation
{
    private readonly ConcurrentDictionary<Guid, Prepared> _pending = new();

    public async Task<ChirpRequestReview> PrepareAsync(ShellViewModel.GoogleTtsStudioRequestSelection selection,
        int ordinal, CancellationToken cancellationToken)
    {
        var source = await ResolveSelectionAsync(selection, ordinal, cancellationToken).ConfigureAwait(false);
        var pending = await compilation.CompileReadonlyAsync(selection with { ExactText = source.ExactText }, cancellationToken).ConfigureAwait(false);
        var review = await articles.PrepareAsync(new(selection.DocumentId, ordinal, selection.ProjectStableId, pending.Envelope,
            pending.Snapshot.ProviderRequest.CompiledPayload, pending.Currency, pending.Scale, pending.CurrentEstimateMinorUnits), cancellationToken).ConfigureAwait(false);
        foreach (var item in _pending)
            if (clock.GetUtcNow() - item.Value.CreatedAt > TimeSpan.FromMinutes(10)) Discard(item.Key);
        if (_pending.Count >= 32)
        { articles.Discard(review.PreviewId); throw new InvalidOperationException("Close earlier internal request reviews before preparing another."); }
        _pending[review.PreviewId] = new(selection.DocumentId, source.Document.CurrentRevisionId!.Value, source.Proposal.SourceSha256,
            ordinal, pending, review, clock.GetUtcNow());
        return review;
    }

    public async Task ApproveAsync(ShellViewModel.GoogleTtsStudioRequestSelection selection,
        ChirpApprovalConfirmation confirmation, CancellationToken cancellationToken)
    {
        if (!_pending.TryGetValue(confirmation.PreviewId, out var prepared))
            throw LocalRequestDiagnostics.Mark(new InvalidOperationException("The displayed native article and internal request review changed."), LocalRequestReason.ReviewMissing);
        if (clock.GetUtcNow() - prepared.CreatedAt > TimeSpan.FromMinutes(10))
            throw LocalRequestDiagnostics.Mark(new InvalidOperationException("The displayed native article and internal request review changed."), LocalRequestReason.ReviewExpired, prepared.CreatedAt, prepared.Review.ExpiresAtUtc);
        if (prepared.DocumentId != selection.DocumentId || prepared.RevisionId != selection.RevisionId
            || !string.Equals(prepared.Review.ReviewSha256, confirmation.DisplayedReviewSha256, StringComparison.Ordinal))
            throw LocalRequestDiagnostics.Mark(new InvalidOperationException("The displayed native article and internal request review changed."), LocalRequestReason.SelectionChanged, prepared.CreatedAt, prepared.Review.ExpiresAtUtc);
        var source = await ResolveSelectionAsync(selection, prepared.Ordinal, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(source.Proposal.SourceSha256, prepared.SourceSha256, StringComparison.Ordinal))
            throw LocalRequestDiagnostics.Mark(new InvalidOperationException("The saved original article changed after request review."), LocalRequestReason.SourceChanged, prepared.CreatedAt, prepared.Review.ExpiresAtUtc);
        var current = await compilation.CompileReadonlyAsync(selection with { ExactText = source.ExactText }, cancellationToken).ConfigureAwait(false);
        if (current.Envelope != prepared.Pending.Envelope || current.CurrentEstimateMinorUnits != prepared.Pending.CurrentEstimateMinorUnits
            || !Equals(current.Snapshot.UiSelection, prepared.Pending.Snapshot.UiSelection)
            || !current.Snapshot.ProviderRequest.CompiledPayload.Span.SequenceEqual(prepared.Pending.Snapshot.ProviderRequest.CompiledPayload.Span))
            throw LocalRequestDiagnostics.Mark(new InvalidOperationException("Current account, voice, price, capability or exact compiled request changed."), LocalRequestReason.CompilationChanged, prepared.CreatedAt, prepared.Review.ExpiresAtUtc);
        var approval = await articles.ApproveAsync(confirmation, cancellationToken).ConfigureAwait(false);
        var envelope = current.Envelope with { ChirpAuthorizationId = approval.AuthorizationId };
        var request = current.Snapshot.ProviderRequest;
        var child = new GenerationProviderRequest(request.ProviderStableId, request.OperationStableId, request.AccountId,
            approval.IdempotencyKey, request.CompiledPayload.ToArray(), request.OutputFormat);
        var clean = new GoogleGenerationPersistedQueueState(request.AccountId, request.OperationStableId, approval.IdempotencyKey, false, null);
        var snapshot = current.Snapshot with { ProviderRequest = child, PreviousState = clean, CurrentState = clean, PricingApproved = true };
        await state.ActivateArticleAsync(envelope, snapshot, current.CurrentEstimateMinorUnits, cancellationToken).ConfigureAwait(false);
        _pending.TryRemove(confirmation.PreviewId, out _);
    }

    private async Task<ChirpResolvedSegment> ResolveSelectionAsync(ShellViewModel.GoogleTtsStudioRequestSelection selection, int ordinal, CancellationToken token)
    {
        var source = await articles.ResolveSegmentAsync(selection.DocumentId, ordinal, selection.AccountStableId, selection.ProjectStableId,
            selection.VoiceStableId, selection.LanguageCode, token).ConfigureAwait(false);
        if (source.Document.CurrentRevisionId != selection.RevisionId || !string.Equals(source.Document.Text, selection.ExactText, StringComparison.Ordinal))
            throw new InvalidOperationException("Save and select the exact original article revision before preparing an internal request.");
        return source;
    }

    public void Discard(Guid id) { _pending.TryRemove(id, out _); articles.Discard(id); }

    private sealed record Prepared(Guid DocumentId, Guid RevisionId, string SourceSha256, int Ordinal,
        GoogleGenerationProductionPendingApprovalStateOwner.PendingState Pending, ChirpRequestReview Review, DateTimeOffset CreatedAt);
}
