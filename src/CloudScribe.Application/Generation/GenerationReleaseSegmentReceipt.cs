namespace CloudScribe.Application.Generation;

public sealed record GenerationReleaseSegmentReceipt(
    Guid SegmentId,
    string CacheKey,
    string MediaSha256,
    string ProofProvenanceId,
    bool ProofAccepted)
{
    public GenerationReleaseSegmentReceipt Validate() =>
        Validate(SegmentId, CacheKey, MediaSha256, ProofProvenanceId);

    private GenerationReleaseSegmentReceipt Validate(
        Guid segmentId,
        string cacheKey,
        string mediaSha256,
        string proofProvenanceId)
    {
        if (segmentId == Guid.Empty)
            throw new ArgumentException("Segment id is required.", nameof(segmentId));

        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        ValidateSha256(mediaSha256, nameof(mediaSha256));
        ArgumentException.ThrowIfNullOrWhiteSpace(proofProvenanceId);
        return this;
    }

    private static void ValidateSha256(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || value.Any(static c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Expected lowercase or uppercase SHA-256 hex.", name);
    }
}
