using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private Func<GoogleTtsStudioRequestSelection, int, CancellationToken, Task<ChirpRequestReview>>? _prepareChirpArticle;
    private Func<GoogleTtsStudioRequestSelection, ChirpApprovalConfirmation, CancellationToken, Task>? _approveChirpArticle;
    private Func<GenerationProviderRequest, GoogleGenerationQueueOutcome, CancellationToken, Task<string>>? _persistAcceptedChirpMp3;
    private ChirpRequestReview? _currentChirpReview;

    public void ConfigureChirpArticle(
        Func<GoogleTtsStudioRequestSelection, int, CancellationToken, Task<ChirpRequestReview>> prepare,
        Func<GoogleTtsStudioRequestSelection, ChirpApprovalConfirmation, CancellationToken, Task> approve,
        Func<GenerationProviderRequest, GoogleGenerationQueueOutcome, CancellationToken, Task<string>> persist)
    { _prepareChirpArticle = prepare; _approveChirpArticle = approve; _persistAcceptedChirpMp3 = persist; }

    public async Task<ChirpRequestReview> PrepareChirpArticleAsync(int ordinal, CancellationToken cancellationToken)
    {
        EnterGoogleGenerationOperation();
        try
        {
            Interlocked.Increment(ref _googleRequestVersion);
            InvalidatePrivateGoogleDiagnostic();
            long version = Volatile.Read(ref _googleRequestVersion);
            PreparedGoogleGenerationSpendApproved = false; PreparedGoogleGenerationSpendReview = null;
            _currentChirpReview = null; _invalidateGoogleProductionRequest?.Invoke();
            if (IsDocumentDirty || IsDocumentSaving) throw new InvalidOperationException("Save the original article before preparing an internal request.");
            var prepare = _prepareChirpArticle ?? throw new InvalidOperationException("Internal article preparation is unavailable.");
            var review = await prepare(CaptureGoogleTtsStudioRequestSelection(), ordinal, cancellationToken).ConfigureAwait(true);
            RequireUnchangedGoogleRequest(version); _currentChirpReview = review; return review;
        }
        finally { ExitGoogleGenerationOperation(); }
    }

    public async Task ApproveChirpArticleAsync(ChirpApprovalConfirmation confirmation, CancellationToken cancellationToken)
    {
        long version = Volatile.Read(ref _googleRequestVersion);
        EnterGoogleGenerationOperation();
        try
        {
            var review = _currentChirpReview ?? throw new InvalidOperationException("Prepare one fresh exact internal request review.");
            if (!confirmation.ConfirmedByUser || !confirmation.AcknowledgedHistoricalHolds || confirmation.PreviewId != review.PreviewId
                || !string.Equals(confirmation.DisplayedReviewSha256, review.ReviewSha256, StringComparison.Ordinal)
                || confirmation.MaximumMinorUnits != review.MaximumMinorUnits || IsDocumentDirty || IsDocumentSaving)
                throw new InvalidOperationException("The displayed internal request or explicit acknowledgement changed.");
            await (_approveChirpArticle ?? throw new InvalidOperationException("Internal article approval is unavailable."))(
                CaptureGoogleTtsStudioRequestSelection(), confirmation, cancellationToken).ConfigureAwait(true);
            RequireUnchangedGoogleRequest(version);
            PreparedGoogleGenerationSpendReview = new(review.Currency, review.Scale, review.EstimateMinorUnits,
                review.PricingProvenanceId, review.VoiceName, review.PayloadSha256);
            PreparedGoogleGenerationSpendApproved = true; _currentChirpReview = null;
            StatusMessage = "One internal article request reserved; all historical holds retained. Generate once only when separately authorized. No batch or retry was started.";
        }
        finally { ExitGoogleGenerationOperation(); }
    }
}
