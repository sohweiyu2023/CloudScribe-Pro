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
        var resumed = await articles.ResolveReservedResumeSegmentAsync(selection.DocumentId, ordinal, selection.AccountStableId,
            selection.ProjectStableId, selection.VoiceStableId, selection.LanguageCode, cancellationToken).ConfigureAwait(false);
        var source = resumed ?? await ResolveSelectionAsync(selection, ordinal, cancellationToken).ConfigureAwait(false);
        if (resumed is not null) ChirpResumeSelectionBinding.RequireFresh(selection, clock.GetUtcNow());
        RequireSourceSelection(source, selection);
        var pending = await compilation.CompileReadonlyAsync(selection with { ExactText = source.ExactText }, cancellationToken).ConfigureAwait(false);
        var intent = new ChirpRequestIntent(selection.DocumentId, ordinal, selection.ProjectStableId, pending.Envelope,
            pending.Snapshot.ProviderRequest.CompiledPayload, pending.Currency, pending.Scale, pending.CurrentEstimateMinorUnits);
        var review = resumed is null ? await articles.PrepareAsync(intent, cancellationToken).ConfigureAwait(false)
            : await articles.PrepareReservedResumeAsync(intent, ChirpResumeSelectionBinding.Hash(selection), ResumeRuntimeHash(pending),
                Guid.Parse(selection.CapabilityEvidenceId), ResumeRequestIdentity(pending), cancellationToken).ConfigureAwait(false);
        foreach (var item in _pending)
            if (clock.GetUtcNow() - item.Value.CreatedAt > TimeSpan.FromMinutes(10)) Discard(item.Key);
        if (_pending.Count >= 32)
        { articles.Discard(review.PreviewId); throw new InvalidOperationException("Close earlier internal request reviews before preparing another."); }
        _pending[review.PreviewId] = new(selection.DocumentId, source.Document.CurrentRevisionId!.Value, source.Proposal.SourceSha256,
            ordinal, pending, review, clock.GetUtcNow(), resumed is null ? null : ChirpResumeSelectionBinding.Hash(selection));
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
        if (prepared.ResumeSelectionSha256 is { } selected
            && (!string.Equals(selected, ChirpResumeSelectionBinding.Hash(selection), StringComparison.Ordinal)
                || !string.Equals(ResumeRuntimeHash(current), ResumeRuntimeHash(prepared.Pending), StringComparison.Ordinal)
                || !string.Equals(ResumeRequestIdentity(current), ResumeRequestIdentity(prepared.Pending), StringComparison.Ordinal)))
            throw LocalRequestDiagnostics.Mark(new InvalidOperationException("The exact revalidation compile context changed."), LocalRequestReason.CompilationChanged);
        if (prepared.ResumeSelectionSha256 is not null) ChirpResumeSelectionBinding.RequireFresh(selection, clock.GetUtcNow());
        var approval = prepared.ResumeSelectionSha256 is null ? await articles.ApproveAsync(confirmation, cancellationToken).ConfigureAwait(false)
            : await articles.ApproveReservedResumeAsync(confirmation, cancellationToken).ConfigureAwait(false);
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
        var source = await articles.ResolveReservedResumeSegmentAsync(selection.DocumentId, ordinal, selection.AccountStableId, selection.ProjectStableId,
            selection.VoiceStableId, selection.LanguageCode, token).ConfigureAwait(false)
            ?? await articles.ResolveSegmentAsync(selection.DocumentId, ordinal, selection.AccountStableId, selection.ProjectStableId,
            selection.VoiceStableId, selection.LanguageCode, token).ConfigureAwait(false);
        RequireSourceSelection(source, selection);
        return source;
    }

    private static void RequireSourceSelection(ChirpResolvedSegment source, ShellViewModel.GoogleTtsStudioRequestSelection selection)
    {
        if (source.Document.CurrentRevisionId != selection.RevisionId || !string.Equals(source.Document.Text, selection.ExactText, StringComparison.Ordinal))
            throw LocalRequestDiagnostics.Mark(new InvalidOperationException("Save and select the exact original article revision before preparing an internal request."), LocalRequestReason.ReservedSourceChanged);
    }

    private static string ResumeRuntimeHash(GoogleGenerationProductionPendingApprovalStateOwner.PendingState pending) =>
        ChirpResumeRuntimeBinding.Hash(pending.Snapshot, pending.Currency, pending.Scale, pending.CurrentEstimateMinorUnits);

    private static string ResumeRequestIdentity(GoogleGenerationProductionPendingApprovalStateOwner.PendingState pending)
    {
        const string prefix = "studio-google-tts:";
        string key = pending.Snapshot.ProviderRequest.IdempotencyKey;
        if (!key.StartsWith(prefix, StringComparison.Ordinal) || key.Length != prefix.Length + 64 || !key.AsSpan(prefix.Length).ToArray().All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("Revalidation requires the full trusted compilation request identity.");
        return key[prefix.Length..].ToLowerInvariant();
    }

    public void Discard(Guid id) { _pending.TryRemove(id, out _); articles.Discard(id); }

    private sealed record Prepared(Guid DocumentId, Guid RevisionId, string SourceSha256, int Ordinal,
        GoogleGenerationProductionPendingApprovalStateOwner.PendingState Pending, ChirpRequestReview Review, DateTimeOffset CreatedAt, string? ResumeSelectionSha256);
}
