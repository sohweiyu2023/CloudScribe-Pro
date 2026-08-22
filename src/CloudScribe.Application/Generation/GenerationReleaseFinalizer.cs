namespace CloudScribe.Application.Generation;

public sealed record GenerationReleaseFinalizationResult(
    GenerationReleaseReceipt Receipt,
    GenerationReleaseVerificationResult Verification)
{
    public bool IsFinalized => Verification.IsValid && Receipt.Verify();
}

public sealed class GenerationReleaseFinalizer
{
    private readonly GenerationReleasePublisher _publisher;
    private readonly GenerationReleaseVerifier _verifier;

    public GenerationReleaseFinalizer(
        GenerationReleasePublisher publisher,
        GenerationReleaseVerifier verifier)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }

    public GenerationReleaseFinalizationResult Finalize(
        GenerationCollectionReleaseDecision decision,
        string approvalId,
        string outputPath,
        IEnumerable<GenerationPublishedSegment> publishedSegments)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(publishedSegments);

        var receipt = _publisher.Publish(decision, approvalId, outputPath, publishedSegments);
        var verification = _verifier.Verify(receipt);
        if (!verification.IsValid)
        {
            throw new InvalidDataException($"Published release failed immediate integrity verification: {verification.DiagnosticCode}");
        }

        if (!receipt.Verify())
        {
            throw new InvalidDataException("Published release receipt failed integrity verification after publication.");
        }

        return new GenerationReleaseFinalizationResult(receipt, verification);
    }
}
