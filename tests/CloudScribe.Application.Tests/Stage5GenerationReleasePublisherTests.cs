using System.Security.Cryptography;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5GenerationReleasePublisherTests
{
    [Fact]
    public void PublishHashesActualOutputAndBindsProofProvenance()
    {
        using var temp = new TempDirectory();
        var segmentId = Guid.NewGuid();
        var output = Path.Combine(temp.Path, "release.wav");
        File.WriteAllBytes(output, [1, 2, 3, 4, 5]);
        var decision = SafeDecision(segmentId);

        var receipt = new GenerationReleasePublisher(1024).Publish(
            decision,
            "approval:7",
            output,
            [new GenerationPublishedSegment(segmentId, "cache:segment", new string('a', 64))]);

        var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant();
        Assert.Equal(expected, receipt.OutputSha256);
        Assert.Equal("proof:provider", receipt.Segments[0].ProofProvenanceId);
        Assert.True(receipt.Verify());
    }

    [Fact]
    public void PublishRejectsProofIdentityDriftAndOversizedOutput()
    {
        using var temp = new TempDirectory();
        var segmentId = Guid.NewGuid();
        var output = Path.Combine(temp.Path, "release.wav");
        File.WriteAllBytes(output, [1, 2, 3, 4, 5]);
        var decision = SafeDecision(segmentId);

        Assert.Throws<InvalidOperationException>(() => new GenerationReleasePublisher(1024).Publish(
            decision,
            "approval:7",
            output,
            [new GenerationPublishedSegment(Guid.NewGuid(), "cache:other", new string('b', 64))]));

        Assert.Throws<InvalidDataException>(() => new GenerationReleasePublisher(4).Publish(
            decision,
            "approval:7",
            output,
            [new GenerationPublishedSegment(segmentId, "cache:segment", new string('a', 64))]));
    }

    private static GenerationCollectionReleaseDecision SafeDecision(Guid segmentId) => new(
        Guid.NewGuid(),
        7,
        "pricing:v2.22",
        [new GenerationProofResult(segmentId, new OutputQualityAssessment(OutputQualityDisposition.Accepted, []), true, "proof:provider")],
        [],
        DateTimeOffset.UtcNow);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cloudscribe-stage5-publish-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
