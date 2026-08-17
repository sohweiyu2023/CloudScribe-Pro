namespace CloudScribe.Application.Pricing;

public sealed record PricingCatalogActivationRequest
{
    public PricingCatalogActivationRequest(
        Guid snapshotId,
        string expectedSha256,
        long expectedCurrentActivationSequence,
        PricingCatalogActivationKind kind,
        PricingCatalogApprovalKind approvalKind,
        bool userConfirmed,
        string reason)
    {
        if (snapshotId == Guid.Empty)
        {
            throw new ArgumentException("Snapshot id cannot be empty.", nameof(snapshotId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        string normalizedHash = expectedSha256.Trim().ToLowerInvariant();
        if (normalizedHash.Length != 64 || normalizedHash.Any(static value => !Uri.IsHexDigit(value)))
        {
            throw new ArgumentException("Expected catalog SHA-256 must contain exactly 64 hexadecimal characters.", nameof(expectedSha256));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(expectedCurrentActivationSequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        string normalizedReason = reason.Trim();
        if (normalizedReason.Length > 240 || normalizedReason.Any(static character => char.IsControl(character)))
        {
            throw new ArgumentException("Activation reason must be 1-240 visible characters.", nameof(reason));
        }

        SnapshotId = snapshotId;
        ExpectedSha256 = normalizedHash;
        ExpectedCurrentActivationSequence = expectedCurrentActivationSequence;
        Kind = kind;
        ApprovalKind = approvalKind;
        UserConfirmed = userConfirmed;
        Reason = normalizedReason;
    }

    public Guid SnapshotId { get; }
    public string ExpectedSha256 { get; }
    public long ExpectedCurrentActivationSequence { get; }
    public PricingCatalogActivationKind Kind { get; }
    public PricingCatalogApprovalKind ApprovalKind { get; }
    public bool UserConfirmed { get; }
    public string Reason { get; }
}
