namespace CloudScribe.Infrastructure.Generation;

public sealed record ChirpApprovalConfirmation(Guid PreviewId, string DisplayedReviewSha256,
    long MaximumMinorUnits, bool ConfirmedByUser, bool AcknowledgedHistoricalHolds);
