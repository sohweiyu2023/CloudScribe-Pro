namespace CloudScribe.Application.Generation;

public sealed record GenerationReleaseFinalizationResult(
    GenerationReleaseReceipt Receipt,
    GenerationReleaseVerificationResult Verification)
{
    public bool IsFinalized => Verification.IsValid && Receipt.Verify();
}
