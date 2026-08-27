using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5AudioAssemblyNativeExecutorTests
{
    [Fact]
    public async Task ExecuteAsyncRejectsCollisionWithoutExplicitOverwrite()
    {
        var root = CreateRoot();
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var plan = CreatePlan(root);
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(plan.OutputPaths[0], "existing", cancellationToken).ConfigureAwait(true);
            var executor = new AudioAssemblyNativeExecutor(new WritingNativeTool(CreateWave()));

            await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync(
                plan,
                Path.Combine(root, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"),
                TimeSpan.FromMinutes(1),
                cancellationToken: cancellationToken)).ConfigureAwait(true);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ExecuteAsyncRejectsSuccessWithWrongContainer()
    {
        var root = CreateRoot();
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var plan = CreatePlan(root);
            var executor = new AudioAssemblyNativeExecutor(new WritingNativeTool(new byte[32]));

            await Assert.ThrowsAsync<InvalidDataException>(() => executor.ExecuteAsync(
                plan,
                Path.Combine(root, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"),
                TimeSpan.FromMinutes(1),
                cancellationToken: cancellationToken)).ConfigureAwait(true);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsyncAcceptsExpectedBoundedContainer()
    {
        var root = CreateRoot();
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var plan = CreatePlan(root);
            var executor = new AudioAssemblyNativeExecutor(new WritingNativeTool(CreateWave()));

            var result = await executor.ExecuteAsync(
                plan,
                Path.Combine(root, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"),
                TimeSpan.FromMinutes(1),
                cancellationToken: cancellationToken).ConfigureAwait(true);

            var artifact = Assert.Single(result.Artifacts);
            Assert.Equal(ReleaseAudioFormat.Wav, artifact.Format);
            Assert.Equal(plan.OutputPaths[0], artifact.OutputPath);
            Assert.True(artifact.LengthBytes >= 44);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static AudioAssemblyPlan CreatePlan(string root)
    {
        var input = Path.Combine(root, "input.wav");
        return new AudioAssemblyPlan(
            [new AudioSegmentArtifact("s1", input, "audio/wav", TimeSpan.FromSeconds(1), new string('a', 64))],
            new GenerationMasteringProfile("spoken", -1m, -16m, 0, 0),
            ReleaseAudioFormat.Wav,
            TimeSpan.FromMinutes(10),
            root,
            "release");
    }

    private static string CreateRoot() =>
        Path.Combine(Path.GetTempPath(), "cloudscribe-stage5-executor-" + Guid.NewGuid().ToString("N"));

    private static byte[] CreateWave()
    {
        var bytes = new byte[44];
        "RIFF"u8.CopyTo(bytes);
        BitConverter.GetBytes(36).CopyTo(bytes, 4);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        "fmt "u8.CopyTo(bytes.AsSpan(12));
        BitConverter.GetBytes(16).CopyTo(bytes, 16);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 20);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 22);
        BitConverter.GetBytes(16_000).CopyTo(bytes, 24);
        BitConverter.GetBytes(32_000).CopyTo(bytes, 28);
        BitConverter.GetBytes((short)2).CopyTo(bytes, 32);
        BitConverter.GetBytes((short)16).CopyTo(bytes, 34);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BitConverter.GetBytes(0).CopyTo(bytes, 40);
        return bytes;
    }

    private sealed class WritingNativeTool(byte[] payload) : INativeMediaTool
    {
        public async Task<NativeMediaToolResult> RunAsync(
            NativeMediaToolInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            var output = invocation.Arguments[^1];
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllBytesAsync(output, payload, cancellationToken).ConfigureAwait(false);
            return new NativeMediaToolResult(0, false, string.Empty, string.Empty, TimeSpan.FromMilliseconds(1));
        }
    }
}
