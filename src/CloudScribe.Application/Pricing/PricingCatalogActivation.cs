namespace CloudScribe.Application.Pricing;

public sealed record PricingCatalogActivation(
    long Sequence,
    Guid SnapshotId,
    Guid? PreviousSnapshotId,
    PricingCatalogActivationKind Kind,
    PricingCatalogApprovalKind ApprovalKind,
    string Reason,
    DateTimeOffset OccurredAtUtc);
