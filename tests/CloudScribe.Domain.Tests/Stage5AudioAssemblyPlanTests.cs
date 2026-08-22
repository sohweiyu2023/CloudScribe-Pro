using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage5AudioAssemblyPlanTests
{
    [Fact]
    public void MeasuredDurationsRegroupWithoutSplittingSegments()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cloudscribe-stage5-audio-plan"));
        var segments = new[]
        {
            Segment("s1", root, TimeSpan.FromSeconds(40), 'a'),
            Segment("s2", root, TimeSpan.FromSeconds(35), 'b'),
            Segment("s3", root, TimeSpan.FromSeconds(55), 'c'),
        };
        var profile = new GenerationMasteringProfile("speech", -1m, -16m, 10, 20);

        var plan = new AudioAssemblyPlan(segments, profile, ReleaseAudioFormat.Mp3, TimeSpan.FromSeconds(80), root, "chapter");

        Assert.Equal(2, plan.Parts.Count);
        Assert.Equal(new[] { "s1", "s2" }, plan.Parts[0].Segments.Select(static item => item.SegmentId));
        Assert.Single(plan.Parts[1].Segments);
        Assert.Equal(TimeSpan.FromSeconds(130), plan.TotalMeasuredDuration);
        Assert.EndsWith("chapter.part-001.mp3", plan.OutputPaths[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateSegmentIdentityFailsClosed()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cloudscribe-stage5-audio-plan-dup"));
        var segments = new[]
        {
            Segment("same", root, TimeSpan.FromSeconds(10), 'a'),
            Segment("same", root, TimeSpan.FromSeconds(11), 'b'),
        };
        var profile = new GenerationMasteringProfile("speech", -1m, null, 0, 0);

        Assert.Throws<ArgumentException>(() => new AudioAssemblyPlan(segments, profile, ReleaseAudioFormat.Wav, TimeSpan.FromMinutes(1), root, "out"));
    }

    [Fact]
    public void UnsafeRelativePathsAndOutputNamesAreRejected()
    {
        var profile = new GenerationMasteringProfile("speech", -1m, null, 0, 0);
        var badSegment = new AudioSegmentArtifact("s1", "relative.wav", "audio/wav", TimeSpan.FromSeconds(1), new string('a', 64));

        Assert.Throws<ArgumentException>(() => new AudioAssemblyPlan([badSegment], profile, ReleaseAudioFormat.Wav, TimeSpan.FromMinutes(1), Path.GetTempPath(), "out"));
    }

    private static AudioSegmentArtifact Segment(string id, string root, TimeSpan duration, char hashCharacter) =>
        new(id, Path.Combine(root, id + ".wav"), "audio/wav", duration, new string(hashCharacter, 64));
}
