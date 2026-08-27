using System.Security.Cryptography;
using System.Text;

namespace CloudScribe.Application.Generation;

public sealed record GenerationReleaseReceipt(
    Guid CollectionId,
    long Revision,
    string PricingProvenanceId,
    string ApprovalId,
    string OutputPath,
    string OutputSha256,
    IReadOnlyList<GenerationReleaseSegmentReceipt> Segments,
    string ReceiptSha256)
{
    public static GenerationReleaseReceipt Create(
        Guid collectionId,
        long revision,
        string pricingProvenanceId,
        string approvalId,
        string outputPath,
        string outputSha256,
        IEnumerable<GenerationReleaseSegmentReceipt> segments)
    {
        if (collectionId == Guid.Empty) throw new ArgumentException("Collection id is required.", nameof(collectionId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(pricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ValidateSha256(outputSha256, nameof(outputSha256));
        ArgumentNullException.ThrowIfNull(segments);

        var items = segments.Select(static x => x.Validate()).OrderBy(static x => x.SegmentId).ToArray();
        if (items.Length == 0) throw new ArgumentException("At least one segment receipt is required.", nameof(segments));
        if (items.Select(static x => x.SegmentId).Distinct().Count() != items.Length)
            throw new ArgumentException("Segment ids must be unique.", nameof(segments));
        if (items.Any(static x => !x.ProofAccepted))
            throw new InvalidOperationException("A quarantined segment cannot be included in a release receipt.");

        var canonical = Canonicalize(collectionId, revision, pricingProvenanceId, approvalId, outputPath, outputSha256, items);
        var receiptSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new GenerationReleaseReceipt(collectionId, revision, pricingProvenanceId, approvalId, outputPath, outputSha256.ToLowerInvariant(), items, receiptSha);
    }

    public bool Verify()
    {
        try
        {
            var rebuilt = Create(CollectionId, Revision, PricingProvenanceId, ApprovalId, OutputPath, OutputSha256, Segments);
            return string.Equals(rebuilt.ReceiptSha256, ReceiptSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static string Canonicalize(Guid collectionId, long revision, string pricing, string approval, string path, string outputSha, IReadOnlyList<GenerationReleaseSegmentReceipt> segments)
    {
        var sb = new StringBuilder();
        sb.Append(collectionId.ToString("D")).Append('\n').Append(revision).Append('\n')
          .Append(pricing).Append('\n').Append(approval).Append('\n').Append(path).Append('\n')
          .Append(outputSha.ToLowerInvariant()).Append('\n');
        foreach (var segment in segments)
            sb.Append(segment.SegmentId.ToString("D")).Append('|').Append(segment.CacheKey).Append('|')
              .Append(segment.MediaSha256.ToLowerInvariant()).Append('|').Append(segment.ProofProvenanceId).Append('|')
              .Append(segment.ProofAccepted ? '1' : '0').Append('\n');
        return sb.ToString();
    }

    private static void ValidateSha256(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || value.Any(static c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Expected SHA-256 hex.", name);
    }
}
