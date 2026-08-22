using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5AudioAssemblyNativePlannerTests
{
    [Fact]
    public void Plan_UsesDiscreteAbsoluteArguments_AndMasteringFilter()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cloudscribe-stage5-native"));
        var source1 = Path.Combine(root, "input one.wav");
        var source2 = Path.Combine(root, "input;two.wav");
        var profile = new GenerationMasteringProfile("spoken", -1m, -16m, 100, 200);
        var plan = new AudioAssemblyPlan(
        [
            new AudioSegmentArtifact("s1", source1, "audio/wav", TimeSpan.FromSeconds(2), new string('a', 64)),
            new AudioSegmentArtifact("s2", source2, "audio/wav", TimeSpan.FromSeconds(3), new string('b', 64)),
        ],
        profile,
        ReleaseAudioFormat.Mp3,
        TimeSpan.FromMinutes(30),
        root,
        "release");

        var ffmpeg = Path.Combine(root, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        var invocations = AudioAssemblyNativePlanner.Plan(plan, ffmpeg, TimeSpan.FromMinutes(5));

        var invocation = Assert.Single(invocations);
        Assert.Equal(root, invocation.WorkingDirectory);
        Assert.Contains(source1, invocation.Arguments);
        Assert.Contains(source2, invocation.Arguments);
        Assert.Contains("-filter_complex", invocation.Arguments);
        Assert.Contains(invocation.Arguments, value => value.Contains("loudnorm=I=-16:TP=-1", StringComparison.Ordinal));
        Assert.Equal(plan.OutputPaths[0], invocation.Arguments[^1]);
        Assert.DoesNotContain(invocation.Arguments, value => value.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(invocation.Arguments, value => value.Contains("/bin/sh", StringComparison.Ordinal));
    }
}
