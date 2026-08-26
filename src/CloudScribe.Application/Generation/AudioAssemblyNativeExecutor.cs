using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed class AudioAssemblyNativeExecutor
{
    private readonly INativeMediaTool _nativeMediaTool;

    public AudioAssemblyNativeExecutor(INativeMediaTool nativeMediaTool)
    {
        _nativeMediaTool = nativeMediaTool ?? throw new ArgumentNullException(nameof(nativeMediaTool));
    }

    public async Task<AudioAssemblyExecutionResult> ExecuteAsync(
        AudioAssemblyPlan assembly,
        string ffmpegExecutablePath,
        TimeSpan timeout,
        bool allowOverwrite = false,
        long maximumOutputBytesPerPart = 512L * 1024L * 1024L,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (maximumOutputBytesPerPart is < 64 or > 4L * 1024L * 1024L * 1024L)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputBytesPerPart));
        }

        Directory.CreateDirectory(assembly.OutputDirectory);
        if (!allowOverwrite)
        {
            var collision = assembly.OutputPaths.FirstOrDefault(File.Exists);
            if (collision is not null)
            {
                throw new IOException($"Native assembly output already exists and overwrite is not authorized: {collision}");
            }
        }

        var invocations = AudioAssemblyNativePlanner.Plan(
            assembly,
            ffmpegExecutablePath,
            timeout,
            allowOverwrite: allowOverwrite);
        if (invocations.Count != assembly.OutputPaths.Count)
        {
            throw new InvalidOperationException("Native assembly invocation count does not match planned output count.");
        }

        var artifacts = new List<AudioAssemblyExecutionArtifact>(invocations.Count);
        var nativeResults = new List<NativeMediaToolResult>(invocations.Count);
        for (var index = 0; index < invocations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _nativeMediaTool.RunAsync(invocations[index], cancellationToken).ConfigureAwait(false);
            nativeResults.Add(result);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Native audio assembly failed for part {index + 1}: exit={result.ExitCode}, timedOut={result.TimedOut}.");
            }

            var outputPath = assembly.OutputPaths[index];
            var artifact = ValidateOutput(index + 1, outputPath, assembly.OutputFormat, maximumOutputBytesPerPart);
            artifacts.Add(artifact);
        }

        return new AudioAssemblyExecutionResult(artifacts.ToArray(), nativeResults.ToArray());
    }

    private static AudioAssemblyExecutionArtifact ValidateOutput(
        int partNumber,
        string outputPath,
        ReleaseAudioFormat expectedFormat,
        long maximumOutputBytes)
    {
        if (!File.Exists(outputPath))
        {
            throw new InvalidDataException($"Native media tool reported success but output is missing: {outputPath}");
        }

        var info = new FileInfo(outputPath);
        if (info.Length < 12)
        {
            throw new InvalidDataException("Native assembled media is empty or truncated.");
        }
        if (info.Length > maximumOutputBytes)
        {
            throw new InvalidDataException("Native assembled media exceeds the configured bounded output size.");
        }

        Span<byte> prefix = stackalloc byte[12];
        using (var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
        {
            var total = 0;
            while (total < prefix.Length)
            {
                var read = stream.Read(prefix[total..]);
                if (read == 0)
                {
                    break;
                }
                total += read;
            }
            if (total < prefix.Length)
            {
                throw new InvalidDataException("Native assembled media is truncated before its container signature can be validated.");
            }
        }

        var valid = expectedFormat switch
        {
            ReleaseAudioFormat.Wav => prefix[..4].SequenceEqual("RIFF"u8) && prefix.Slice(8, 4).SequenceEqual("WAVE"u8),
            ReleaseAudioFormat.Mp3 => prefix[..3].SequenceEqual("ID3"u8) || (prefix[0] == 0xFF && (prefix[1] & 0xE0) == 0xE0),
            ReleaseAudioFormat.Flac => prefix[..4].SequenceEqual("fLaC"u8),
            ReleaseAudioFormat.M4a => prefix.Slice(4, 4).SequenceEqual("ftyp"u8),
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException($"Native assembled media does not match expected {expectedFormat} container signature.");
        }

        return new AudioAssemblyExecutionArtifact(partNumber, outputPath, info.Length, expectedFormat);
    }
}
