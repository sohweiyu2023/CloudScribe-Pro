using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleLongAudioDocumentService
{
    public async Task<GoogleLongAudioContinuationReview> InspectContinuationAsync(GoogleLongAudioDocumentReview reviewed, CancellationToken token = default)
    {
        var current = await ReviewAsync(reviewed.DocumentId, token).ConfigureAwait(false);
        if (!string.Equals(current.ScopeSha256, reviewed.ScopeSha256, StringComparison.Ordinal)
            || !string.Equals(current.Request.BindingSha256, reviewed.Request.BindingSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The saved source or configuration changed. Close and review it again.");
        var context = await RequireOutputContextAsync(current, token).ConfigureAwait(false);
        GoogleLongAudioCheckpoint? state = null;
        if (Directory.Exists(context.OperationsDirectory)) state = new GoogleLongAudioFileJournal(context.OperationsDirectory).ReadIfExists(current.Request.AttemptId);
        bool pending = File.Exists(Path.Combine(context.OperationsDirectory, "observation-pending.guard"));
        if (current.DeferredUnknownRanges is { Count: > 0 })
        {
            var authority = new GoogleLongAudioRecoveryAuthority(current.Request, context.OperationsDirectory, context.OutputDirectory);
            var deferred = new GoogleLongAudioDeferredWork(current.Request, context.Scope, context.OperationsDirectory, context.OutputDirectory, context.LedgerPath, context.ScopeSha256, authority);
            deferred.RequireReady(current.Request, newPost: false);
            pending = false; // Only the exact immutable reviewed unknown is excluded; any new uncertainty above rejects.
        }
        if (!current.Cost.IsAlreadyCommitted && state is null && !pending)
            return new(current, GoogleLongAudioContinuationKind.CorrectLocalSetup, state);
        if (current.AdaptiveRecoveryEnabled && current.Cost.IsAlreadyCommitted && !pending
            && state is
            {
                State: GoogleLongAudioState.Failed, FailureKind: GoogleLongAudioFailureKind.SentenceLengthReported,
                OperationParseFailed: false, Observation: { ReadbackVerified: true, ReadState: GoogleResponseReadState.Complete, Truncated: false }
            }
            && string.Equals(state.BindingSha256, current.Request.BindingSha256, StringComparison.Ordinal))
            return new(current, GoogleLongAudioContinuationKind.ResumeExistingOperation, state);
        if (current.Cost.IsAlreadyCommitted && !pending && state is
            {
                State: GoogleLongAudioState.Pollable or GoogleLongAudioState.CompletedUnverified,
                OperationParseFailed: false, Observation: { ReadbackVerified: true }
            }
            && (state.Observation.ReadState == GoogleResponseReadState.Complete || state.HasVerifiedPollDeadline)
            && string.Equals(state.BindingSha256, current.Request.BindingSha256, StringComparison.Ordinal))
            return new(current, GoogleLongAudioContinuationKind.ResumeExistingOperation, state);
        return new(current, GoogleLongAudioContinuationKind.ReconcileRequired, state);
    }
}
