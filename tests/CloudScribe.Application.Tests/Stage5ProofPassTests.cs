using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5ProofPassTests
{
    private static readonly string[] ClippedProviderDiagnostics = ["provider.output.clipped"];

    [Fact]
    public void HealthySegmentIsReleaseSafe()
    {
        var proof = new GenerationProofPass(0.10);
        var input = new GenerationProofInput(
            Guid.NewGuid(),
            MediaValid: true,
            ExpectedDuration: TimeSpan.FromSeconds(10),
            ActualDuration: TimeSpan.FromSeconds(10.5),
            RequiredTimingMarksPresent: true,
            ProviderDiagnostics: Array.Empty<string>(),
            ProvenanceId: "fake/provider/run-1");

        var result = proof.Evaluate(input);

        Assert.True(result.IsReleaseSafe);
        Assert.Equal(OutputQualityDisposition.Accepted, result.Quality.Disposition);
        Assert.True(result.DurationWithinTolerance);
    }

    [Fact]
    public void InvalidMediaMissingMarksAndDurationDriftAreQuarantined()
    {
        var proof = new GenerationProofPass(0.10);
        var result = proof.Evaluate(new GenerationProofInput(
            Guid.NewGuid(),
            MediaValid: false,
            ExpectedDuration: TimeSpan.FromSeconds(10),
            ActualDuration: TimeSpan.FromSeconds(20),
            RequiredTimingMarksPresent: false,
            ProviderDiagnostics: ClippedProviderDiagnostics,
            ProvenanceId: "fake/provider/run-2"));

        Assert.False(result.IsReleaseSafe);
        Assert.Contains("quality.media.invalid", result.Quality.DiagnosticCodes);
        Assert.Contains("quality.duration.out-of-range", result.Quality.DiagnosticCodes);
        Assert.Contains("quality.timing-marks.missing", result.Quality.DiagnosticCodes);
        Assert.Contains("provider.output.clipped", result.Quality.DiagnosticCodes);
    }

    [Fact]
    public void CollectionRejectsDuplicateSegmentIdentity()
    {
        var proof = new GenerationProofPass();
        var id = Guid.NewGuid();
        var inputs = Enumerable.Range(0, 2).Select(_ => new GenerationProofInput(
            id,
            true,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            true,
            Array.Empty<string>(),
            "fake/provenance")).ToArray();

        Assert.Throws<ArgumentException>(() => proof.EvaluateCollection(inputs));
    }

    [Fact]
    public void QuarantineBlocksRelease()
    {
        var proof = new GenerationProofPass();
        var accepted = proof.Evaluate(new GenerationProofInput(
            Guid.NewGuid(), true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), true,
            Array.Empty<string>(), "fake/accepted"));
        var quarantined = proof.Evaluate(new GenerationProofInput(
            Guid.NewGuid(), false, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), true,
            Array.Empty<string>(), "fake/quarantined"));

        var error = Assert.Throws<InvalidOperationException>(() => proof.EnsureReleaseSafe(new[] { accepted, quarantined }));
        Assert.Contains("release is blocked", error.Message, StringComparison.Ordinal);
    }
}
