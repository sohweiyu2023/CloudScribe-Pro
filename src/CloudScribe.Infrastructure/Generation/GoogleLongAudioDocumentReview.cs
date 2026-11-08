namespace CloudScribe.Infrastructure.Generation;

/// <summary>UI review of a saved revision; not permission to submit or a provider compatibility claim.</summary>
public sealed record GoogleLongAudioDocumentReview(Guid DocumentId, Guid RevisionId, string Title, string SourceSha256,
    bool HistoricalRevision, string ScopeSha256, GoogleLongAudioDocumentJob Job, GoogleLongAudioRequest Request,
    GoogleLongAudioCostReview Cost, GoogleLongAudioOutputAllowance OutputAllowance, bool AdaptiveRecoveryEnabled = false,
    int ApprovedReplacementCount = 0, long AdditionalReplacementHoldCents = 0,
    IReadOnlyList<GoogleLongAudioDeferredRange>? DeferredUnknownRanges = null)
{
    public override string ToString() => "Long Audio saved-document review; private fields omitted.";
}
