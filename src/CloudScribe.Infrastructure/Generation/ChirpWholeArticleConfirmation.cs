namespace CloudScribe.Infrastructure.Generation;

public sealed record ChirpWholeArticleConfirmation(Guid PreviewId, string ReviewSha256,
    long MaximumAdditionalMinorUnits, bool ConfirmedByUser, bool AcknowledgedHistoricalHolds);
