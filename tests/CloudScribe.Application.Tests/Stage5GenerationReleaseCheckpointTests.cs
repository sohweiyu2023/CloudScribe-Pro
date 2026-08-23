using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5GenerationReleaseCheckpointTests
{
    [Fact]
    public void MatchingVerifiedReceiptCanAdvanceToFinalized()
    {
        var now = DateTimeOffset.Parse("2026-08-23T00:00:00Z");
        var receipt = Receipt();
        var checkpoint = GenerationReleaseCheckpoint.FromReceipt(receipt, GenerationReleaseCheckpointState.PublishedPendingVerification, now);

        var finalized = checkpoint.MarkFinalized(receipt, new GenerationReleaseVerificationResult(true, "release-verified", receipt.OutputSha256), now.AddSeconds(1));

        Assert.Equal(GenerationReleaseCheckpointState.Finalized, finalized.State);
        Assert.Equal(receipt.ReceiptSha256, finalized.ReceiptSha256);
    }

    [Fact]
    public void ReceiptDriftAndFailedVerificationCannotFinalize()
    {
        var now = DateTimeOffset.Parse("2026-08-23T00:00:00Z");
        var receipt = Receipt();
        var checkpoint = GenerationReleaseCheckpoint.FromReceipt(receipt, GenerationReleaseCheckpointState.PublishedPendingVerification, now);
        var drifted = receipt with { Revision = receipt.Revision + 1 };

        Assert.Throws<InvalidDataException>(() => checkpoint.EnsureMatches(drifted));
        Assert.Throws<InvalidOperationException>(() => checkpoint.MarkFinalized(receipt, new GenerationReleaseVerificationResult(false, "output-hash-mismatch", null), now.AddSeconds(1)));
    }

    private static GenerationReleaseReceipt Receipt() =>
        GenerationReleaseReceipt.Create(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            4,
            "pricing-v1",
            "approval-v1",
            Path.GetFullPath("release.wav"),
            new string('a', 64),
            new[]
            {
                new GenerationReleaseSegmentReceipt(
                    Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    "cache-1",
                    new string('b', 64),
                    "proof-v1",
                    true),
            });
}
