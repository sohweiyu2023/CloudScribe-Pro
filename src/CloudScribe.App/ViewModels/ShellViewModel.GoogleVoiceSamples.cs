namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private GoogleSampleCapture? _googleSampleCapture;
    private bool _googleSamplePrepared;

    public async Task<GoogleGenerationSpendReview> PrepareGoogleVoiceSampleAsync(
        string text, Guid sampleDocumentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > 500 || sampleDocumentId == Guid.Empty)
            throw new InvalidOperationException("Use a sample of 1 to 500 characters.");
        if (_googleSampleCapture is not null)
            throw new InvalidOperationException("A sample capture is already active.");
        _googleSampleCapture = new GoogleSampleCapture(text, sampleDocumentId);
        _googleSamplePrepared = false;
        try
        {
            GoogleGenerationSpendReview review = await PrepareGoogleGenerationSpendReviewAsync(cancellationToken).ConfigureAwait(true);
            _googleSamplePrepared = true;
            return review;
        }
        finally
        {
            _googleSampleCapture = null;
        }
    }

    public void CancelGoogleVoiceSampleReview()
    {
        if (Volatile.Read(ref _googleGenerationInFlight) != 0)
            throw new InvalidOperationException("Wait for the current Google operation before changing the sample.");
        PreparedGoogleGenerationSpendApproved = false;
        PreparedGoogleGenerationSpendReview = null;
        _googleSamplePrepared = false;
    }

    public async Task<string> GeneratePreparedGoogleVoiceSampleAsync(CancellationToken cancellationToken = default)
    {
        if (!_googleSamplePrepared || !CanGenerateWithGoogle)
            throw new InvalidOperationException("Prepare and explicitly approve this exact sample first.");
        string? documentOutput = LastGeneratedGoogleMp3Path;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await GenerateWithGoogleCommand.ExecuteAsync(null).ConfigureAwait(true);
            string? sampleOutput = LastGeneratedGoogleMp3Path;
            if (string.IsNullOrWhiteSpace(sampleOutput) || string.Equals(sampleOutput, documentOutput, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("No new verified sample was accepted. Review queue status before another attempt.");
            return sampleOutput;
        }
        finally
        {
            // Sampling never replaces the document's selected output or edits its text.
            LastGeneratedGoogleMp3Path = documentOutput;
            _googleSamplePrepared = false;
        }
    }

    private sealed record GoogleSampleCapture(string Text, Guid DocumentId);
}
