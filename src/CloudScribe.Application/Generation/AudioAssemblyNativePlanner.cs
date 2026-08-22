using System.Globalization;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public static class AudioAssemblyNativePlanner
{
    public static IReadOnlyList<NativeMediaToolInvocation> Plan(
        AudioAssemblyPlan assembly,
        string ffmpegExecutablePath,
        TimeSpan timeout,
        int maximumCapturedOutputCharacters = 64_000,
        bool allowOverwrite = false)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegExecutablePath);
        if (!Path.IsPathFullyQualified(ffmpegExecutablePath))
        {
            throw new ArgumentException("FFmpeg path must be fully qualified.", nameof(ffmpegExecutablePath));
        }

        var invocations = new List<NativeMediaToolInvocation>(assembly.Parts.Count);
        for (var partIndex = 0; partIndex < assembly.Parts.Count; partIndex++)
        {
            var part = assembly.Parts[partIndex];
            var arguments = new List<string> { "-hide_banner", "-nostdin", allowOverwrite ? "-y" : "-n" };
            foreach (var segment in part.Segments)
            {
                if (!Path.IsPathFullyQualified(segment.SourcePath))
                {
                    throw new InvalidOperationException("Assembly contains a non-absolute segment path.");
                }
                arguments.Add("-i");
                arguments.Add(segment.SourcePath);
            }

            var inputLabels = string.Concat(Enumerable.Range(0, part.Segments.Count).Select(index => $"[{index}:a]"));
            var filter = $"{inputLabels}concat=n={part.Segments.Count}:v=0:a=1";
            if (assembly.MasteringProfile.TargetLufs is { } lufs)
            {
                filter += $",loudnorm=I={lufs.ToString(CultureInfo.InvariantCulture)}:TP={assembly.MasteringProfile.TargetPeakDbfs.ToString(CultureInfo.InvariantCulture)}";
            }
            else
            {
                var linearPeak = Math.Pow(10d, (double)assembly.MasteringProfile.TargetPeakDbfs / 20d);
                filter += $",alimiter=limit={linearPeak.ToString("0.########", CultureInfo.InvariantCulture)}";
            }
            if (assembly.MasteringProfile.FadeInMilliseconds > 0)
            {
                filter += $",afade=t=in:st=0:d={(assembly.MasteringProfile.FadeInMilliseconds / 1000m).ToString(CultureInfo.InvariantCulture)}";
            }
            if (assembly.MasteringProfile.FadeOutMilliseconds > 0)
            {
                var start = Math.Max(0m, (decimal)part.MeasuredDuration.TotalSeconds - assembly.MasteringProfile.FadeOutMilliseconds / 1000m);
                filter += $",afade=t=out:st={start.ToString(CultureInfo.InvariantCulture)}:d={(assembly.MasteringProfile.FadeOutMilliseconds / 1000m).ToString(CultureInfo.InvariantCulture)}";
            }
            filter += "[outa]";

            arguments.Add("-filter_complex");
            arguments.Add(filter);
            arguments.Add("-map");
            arguments.Add("[outa]");
            AddOutputCodec(arguments, assembly.OutputFormat);
            arguments.Add(assembly.OutputPaths[partIndex]);

            var invocation = new NativeMediaToolInvocation(
                Path.GetFullPath(ffmpegExecutablePath),
                arguments,
                assembly.OutputDirectory,
                timeout,
                maximumCapturedOutputCharacters);
            invocation.Validate();
            invocations.Add(invocation);
        }

        return invocations;
    }

    private static void AddOutputCodec(List<string> arguments, ReleaseAudioFormat format)
    {
        switch (format)
        {
            case ReleaseAudioFormat.Wav:
                arguments.AddRange(["-c:a", "pcm_s16le"]);
                break;
            case ReleaseAudioFormat.Mp3:
                arguments.AddRange(["-c:a", "libmp3lame", "-q:a", "2"]);
                break;
            case ReleaseAudioFormat.Flac:
                arguments.AddRange(["-c:a", "flac"]);
                break;
            case ReleaseAudioFormat.M4a:
                arguments.AddRange(["-c:a", "aac", "-b:a", "192k"]);
                break;
            default:
                throw new InvalidOperationException("Unsupported release audio format.");
        }
    }
}
