namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleRecoveryApprovalConfirmation(Guid PreviewId, string DisplayedReviewSha256,
    long AdditionalMaximumMinorUnits, bool ConfirmedByUser, bool AcknowledgedHistoricalHolds);
