namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleTextToSpeechCatalogBootstrapService
{
    /// <summary>Read-only diagnostic activation check; does not claim, renew or retire a run.</summary>
    public bool RequiresReviewedRefresh => completionGate?.IsRequired == true;
}
