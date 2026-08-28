namespace CloudScribe.Application.Generation;

public sealed record GenerationPublishedSegment(
    Guid SegmentId,
    string CacheKey,
    string MediaSha256)
{
    public GenerationPublishedSegment Validate() => Validate(SegmentId, CacheKey, MediaSha256);

    private GenerationPublishedSegment Validate(Guid segmentId, string cacheKey, string mediaSha256)
    {
        if (segmentId == Guid.Empty)
            throw new ArgumentException("Segment id is required.", nameof(segmentId));

        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaSha256);
        return this;
    }
}
