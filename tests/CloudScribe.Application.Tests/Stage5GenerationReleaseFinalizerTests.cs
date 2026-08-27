using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5GenerationReleaseFinalizerTests
{
    [Fact]
    public void FinalizePublishesAndImmediatelyVerifiesActualOutput()
    {
        using var temp = new TempDirectory();
        var segmentId = Guid.NewGuid();
        var output = Path.Combine(temp.Path, "final.wav");
        File.WriteAllBytes(output, [1, 2, 3, 4, 5, 6]);

        var finalizer = new GenerationReleaseFinalizer(
            new GenerationReleasePublisher(1024),
            new GenerationReleaseVerifier(1024));

        var result = finalizer.Finalize(
            SafeDecision(segmentId),
            "approval:9",
            output,
            [new GenerationPublishedSegment(segmentId, "cache:segment", new string('a', 64))]);

        Assert.True(result.IsFinalized);
        Assert.True(result.Verification.IsValid);
        Assert.Equal("release-verified", result.Verification.DiagnosticCode);
        Assert.Equal(result.Receipt.OutputSha256, result.Verification.ObservedOutputSha256);
    }

    private static GenerationCollectionReleaseDecision SafeDecision(Guid segmentId) => new(
        Guid.NewGuid(),
        9,
        "pricing:v2.22",
        [new GenerationProofResult(segmentId, new OutputQualityAssessment(OutputQualityDisposition.Accepted, []), true, "proof:provider")],
        [],
        DateTimeOffset.UtcNow);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cloudscribe-stage5-finalize-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
