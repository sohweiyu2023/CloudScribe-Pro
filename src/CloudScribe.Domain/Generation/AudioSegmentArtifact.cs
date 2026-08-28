namespace CloudScribe.Domain.Generation;

public sealed record AudioSegmentArtifact(
    string SegmentId,
    string SourcePath,
    string MediaType,
    TimeSpan MeasuredDuration,
    string ContentSha256)
{
    public AudioSegmentArtifact Validate() => Validate(
        SegmentId,
        SourcePath,
        MediaType,
        MeasuredDuration,
        ContentSha256);

    private AudioSegmentArtifact Validate(
        string segmentId,
        string sourcePath,
        string mediaType,
        TimeSpan measuredDuration,
        string contentSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new ArgumentException("Audio segment source path must be fully qualified.", nameof(sourcePath));
        }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(measuredDuration, TimeSpan.Zero);
        if (contentSha256.Length != 64 || contentSha256.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Audio segment content identity must be a SHA-256 hex digest.", nameof(contentSha256));
        }
        return this;
    }
}
