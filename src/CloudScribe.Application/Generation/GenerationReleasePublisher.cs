using System.Security.Cryptography;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed record GenerationPublishedSegment(
    Guid SegmentId,
    string CacheKey,
    string MediaSha256);

public sealed class GenerationReleasePublisher
{
    private readonly long _maximumOutputBytes;

    public GenerationReleasePublisher(long maximumOutputBytes = 2L * 1024 * 1024 * 1024)
    {
        if (maximumOutputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumOutputBytes));
        _maximumOutputBytes = maximumOutputBytes;
    }

    public GenerationReleaseReceipt Publish(
        GenerationCollectionReleaseDecision decision,
        string approvalId,
        string outputPath,
        IEnumerable<GenerationPublishedSegment> publishedSegments)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(publishedSegments);

        if (!decision.IsReleaseSafe)
            throw new InvalidOperationException("A release decision containing quarantined output cannot be published.");

        var fullPath = Path.GetFullPath(outputPath);
        if (!Path.IsPathFullyQualified(fullPath))
            throw new InvalidOperationException("Published output path must resolve to an absolute path.");

        var file = new FileInfo(fullPath);
        if (!file.Exists) throw new FileNotFoundException("Published release output does not exist.", fullPath);
        if (file.Length <= 0 || file.Length > _maximumOutputBytes)
            throw new InvalidDataException("Published release output size is outside the allowed bounds.");

        var segments = publishedSegments.ToArray();
        if (segments.Length == 0) throw new ArgumentException("At least one published segment is required.", nameof(publishedSegments));
        if (segments.Select(static x => x.SegmentId).Distinct().Count() != segments.Length)
            throw new ArgumentException("Published segment identities must be unique.", nameof(publishedSegments));

        var proofs = decision.ProofResults.ToDictionary(static x => x.SegmentId);
        if (!proofs.Keys.ToHashSet().SetEquals(segments.Select(static x => x.SegmentId)))
            throw new InvalidOperationException("Published segments must exactly match the release decision Proof Pass identities.");

        var receipts = segments.Select(segment =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(segment.CacheKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(segment.MediaSha256);
            var proof = proofs[segment.SegmentId];
            if (!proof.IsReleaseSafe)
                throw new InvalidOperationException("A quarantined segment cannot be published.");
            return new GenerationReleaseSegmentReceipt(
                segment.SegmentId,
                segment.CacheKey,
                segment.MediaSha256,
                proof.ProvenanceId,
                true);
        }).ToArray();

        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        var outputSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        return GenerationReleaseReceipt.Create(
            decision.CollectionId,
            decision.RequestRevision,
            decision.PricingProvenanceId,
            approvalId,
            fullPath,
            outputSha256,
            receipts);
    }

    public async Task<GenerationReleaseReceipt> PublishAndProtectAsync(
        GenerationCollectionReleaseDecision decision,
        string approvalId,
        string outputPath,
        IEnumerable<GenerationPublishedSegment> publishedSegments,
        IGenerationCacheLifecycle cacheLifecycle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cacheLifecycle);
        var receipt = Publish(decision, approvalId, outputPath, publishedSegments);

        foreach (var segment in receipt.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lookup = new PrivateCacheLookupKey(segment.CacheKey).Validate();
            var key = ContentAddressedSegmentKey.FromPrivateLookup(lookup);
            await cacheLifecycle.SetProtectionAsync(
                key,
                GenerationCacheEntryProtection.Referenced,
                cancellationToken).ConfigureAwait(false);
        }

        return receipt;
    }
}
