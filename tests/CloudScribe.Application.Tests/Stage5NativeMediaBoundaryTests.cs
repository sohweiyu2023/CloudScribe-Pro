using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5NativeMediaBoundaryTests
{
    [Fact]
    public void InvocationRequiresAbsoluteExecutableAndWorkingDirectory()
    {
        var invocation = new NativeMediaToolInvocation(
            "ffmpeg",
            ["-version"],
            ".",
            TimeSpan.FromSeconds(10),
            4096);

        Assert.Throws<ArgumentException>(invocation.Validate);
    }

    [Fact]
    public void InvocationRejectsUnboundedTimeoutAndCapturedOutput()
    {
        var executable = Path.GetFullPath("fake-ffmpeg.exe");
        var workingDirectory = Path.GetFullPath("scratch");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NativeMediaToolInvocation(
                executable,
                [],
                workingDirectory,
                TimeSpan.FromHours(1),
                4096).Validate());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NativeMediaToolInvocation(
                executable,
                [],
                workingDirectory,
                TimeSpan.FromSeconds(30),
                2_000_000).Validate());
    }

    [Fact]
    public void InvocationPreservesArgumentsAsDiscreteValues()
    {
        var invocation = new NativeMediaToolInvocation(
            Path.GetFullPath("fake-ffmpeg.exe"),
            ["-i", "input path; & literal.wav", "output path.wav"],
            Path.GetFullPath("scratch"),
            TimeSpan.FromSeconds(30),
            16_384);

        invocation.Validate();

        Assert.Equal("input path; & literal.wav", invocation.Arguments[1]);
        Assert.Equal("output path.wav", invocation.Arguments[2]);
    }

    [Fact]
    public void ToolResultRequiresZeroExitAndNoTimeoutForSuccess()
    {
        Assert.True(new NativeMediaToolResult(0, false, "", "", TimeSpan.Zero).Succeeded);
        Assert.False(new NativeMediaToolResult(1, false, "", "", TimeSpan.Zero).Succeeded);
        Assert.False(new NativeMediaToolResult(0, true, "", "", TimeSpan.Zero).Succeeded);
    }
}
