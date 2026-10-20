namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    public async Task CreateReviewedGoogleMp3Async(
        GoogleGenerationSpendReview displayedReview,
        bool confirmedByUser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(displayedReview);
        if (!confirmedByUser)
            throw new InvalidOperationException("Approve the displayed exact request before creating an MP3.");
        if (!ReferenceEquals(displayedReview, PreparedGoogleGenerationSpendReview))
            throw new InvalidOperationException("The request changed. Review the current cost again.");
        await ApproveGoogleGenerationSpendAsync(
            displayedReview.CurrentEstimateMinorUnits, confirmedByUser, cancellationToken).ConfigureAwait(true);
        if (!CanGenerateWithGoogle)
            throw new InvalidOperationException("The request is no longer ready. Review the current text, voice and cost again.");
        await GenerateWithGoogleCommand.ExecuteAsync(null).ConfigureAwait(true);
    }
}
