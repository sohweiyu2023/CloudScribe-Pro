using System.ComponentModel;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private long _googleRequestVersion;
    private Action? _invalidateGoogleProductionRequest;

    public void ConfigureGoogleRequestInvalidation(Action invalidate) =>
        _invalidateGoogleProductionRequest = invalidate ?? throw new ArgumentNullException(nameof(invalidate));

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentText) or nameof(CurrentDocumentId)
            or nameof(SelectedVoiceLabVoice) or nameof(VoiceLabLocaleFilter))
        {
            Interlocked.Increment(ref _googleRequestVersion);
            bool hadReview = PreparedGoogleGenerationSpendReview is not null;
            ClearChangedGoogleRequest();
            if (hadReview)
                StatusMessage = "Request changed - prepare and approve the current text and voice again. No new request sent.";
        }
        base.OnPropertyChanged(e);
    }

    private void ClearChangedGoogleRequest()
    {
        InvalidatePrivateGoogleDiagnostic();
        PreparedGoogleGenerationSpendApproved = false;
        PreparedGoogleGenerationSpendReview = null;
        _googleSamplePrepared = false;
        _currentRecoveryReview = null;
        _currentChirpReview = null;
        _invalidateGoogleProductionRequest?.Invoke();
    }

    private void RequireUnchangedGoogleRequest(long version)
    {
        if (version == Volatile.Read(ref _googleRequestVersion)) return;
        ClearChangedGoogleRequest();
        throw new InvalidOperationException("The document or voice changed during review. Prepare the current request again.");
    }
}
