using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private Func<GoogleTtsStudioRequestSelection, string, long, CancellationToken, Task<GoogleRecoveryPreview>>? _prepareRecovery;
    private Func<GoogleTtsStudioRequestSelection, GoogleRecoveryApprovalConfirmation, CancellationToken, Task>? _approveRecovery;
    private GoogleRecoveryPreview? _currentRecoveryReview;

    public void ConfigureGoogleRecovery(
        Func<GoogleTtsStudioRequestSelection, string, long, CancellationToken, Task<GoogleRecoveryPreview>> prepare,
        Func<GoogleTtsStudioRequestSelection, GoogleRecoveryApprovalConfirmation, CancellationToken, Task> approve)
    { _prepareRecovery = prepare; _approveRecovery = approve; }

    public async Task<GoogleRecoveryPreview> PrepareGoogleRecoveryAsync(string historicalKey, long maximum, CancellationToken cancellationToken)
    {
        long version = Volatile.Read(ref _googleRequestVersion);
        EnterGoogleGenerationOperation();
        try
        {
            PreparedGoogleGenerationSpendApproved = false; PreparedGoogleGenerationSpendReview = null;
            _currentRecoveryReview = null; _invalidateGoogleProductionRequest?.Invoke();
            var prepare = _prepareRecovery ?? throw new InvalidOperationException("Recovery preparation is unavailable.");
            var review = await prepare(CaptureGoogleTtsStudioRequestSelection(), historicalKey, maximum, cancellationToken).ConfigureAwait(true);
            RequireUnchangedGoogleRequest(version);
            _currentRecoveryReview = review;
            return review;
        }
        finally { ExitGoogleGenerationOperation(); }
    }

    public async Task ApproveGoogleRecoveryAsync(GoogleRecoveryApprovalConfirmation confirmation, CancellationToken cancellationToken)
    {
        long version = Volatile.Read(ref _googleRequestVersion);
        EnterGoogleGenerationOperation();
        try
        {
            var review = _currentRecoveryReview ?? throw new InvalidOperationException("Prepare a fresh exact recovery review.");
            if (!confirmation.ConfirmedByUser || !confirmation.AcknowledgedHistoricalHolds
                || review.PreviewId != confirmation.PreviewId || !string.Equals(review.ReviewSha256, confirmation.DisplayedReviewSha256, StringComparison.Ordinal)
                || review.AdditionalMaximumMinorUnits != confirmation.AdditionalMaximumMinorUnits)
                throw new InvalidOperationException("The displayed recovery review changed.");
            var approve = _approveRecovery ?? throw new InvalidOperationException("Recovery approval is unavailable.");
            await approve(CaptureGoogleTtsStudioRequestSelection(), confirmation, cancellationToken).ConfigureAwait(true);
            RequireUnchangedGoogleRequest(version);
            PreparedGoogleGenerationSpendReview = new(review.Intent.Currency, review.Intent.Scale, review.Intent.EstimateMinorUnits,
                review.Intent.Envelope.PricingProvenanceId, review.Intent.Envelope.VoiceName, review.Intent.Envelope.CompiledPayloadSha256);
            PreparedGoogleGenerationSpendApproved = true;
            _currentRecoveryReview = null;
            StatusMessage = "Additional attempt reserved; all earlier holds retained. No audio requested. Use Generate only when ready.";
        }
        finally { ExitGoogleGenerationOperation(); }
    }
}
