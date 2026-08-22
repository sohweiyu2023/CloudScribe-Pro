namespace CloudScribe.Domain.Generation;

public enum ReleaseAudioFormat
{
    Wav,
    Mp3,
    Flac,
    M4a,
}

public sealed record AudioSegmentArtifact(
    string SegmentId,
    string SourcePath,
    string MediaType,
    TimeSpan MeasuredDuration,
    string ContentSha256)
{
    public AudioSegmentArtifact Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SegmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(MediaType);
        if (!Path.IsPathFullyQualified(SourcePath))
        {
            throw new ArgumentException("Audio segment source path must be fully qualified.", nameof(SourcePath));
        }
        if (MeasuredDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MeasuredDuration));
        }
        if (ContentSha256.Length != 64 || ContentSha256.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Audio segment content identity must be a SHA-256 hex digest.", nameof(ContentSha256));
        }
        return this;
    }
}

public sealed record AudioAssemblyPart(int PartNumber, IReadOnlyList<AudioSegmentArtifact> Segments, TimeSpan MeasuredDuration);

public sealed class AudioAssemblyPlan
{
    public AudioAssemblyPlan(
        IEnumerable<AudioSegmentArtifact> segments,
        MasteringProfile masteringProfile,
        ReleaseAudioFormat outputFormat,
        TimeSpan targetPartDuration,
        string outputDirectory,
        string outputStem)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(masteringProfile);
        masteringProfile.Validate();
        if (!Enum.IsDefined(outputFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(outputFormat));
        }
        if (targetPartDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPartDuration));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputStem);
        if (!Path.IsPathFullyQualified(outputDirectory))
        {
            throw new ArgumentException("Audio output directory must be fully qualified.", nameof(outputDirectory));
        }
        if (outputStem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || outputStem is "." or "..")
        {
            throw new ArgumentException("Audio output stem is not a safe file name.", nameof(outputStem));
        }

        Segments = segments.Select(static segment => segment.Validate()).ToArray();
        if (Segments.Count == 0)
        {
            throw new ArgumentException("Audio assembly requires at least one validated segment.", nameof(segments));
        }
        if (Segments.Select(static segment => segment.SegmentId).Distinct(StringComparer.Ordinal).Count() != Segments.Count)
        {
            throw new ArgumentException("Audio assembly segment identifiers must be unique.", nameof(segments));
        }

        MasteringProfile = masteringProfile;
        OutputFormat = outputFormat;
        TargetPartDuration = targetPartDuration;
        OutputDirectory = Path.GetFullPath(outputDirectory);
        OutputStem = outputStem;
        Parts = BuildParts(Segments, targetPartDuration);
        OutputPaths = Parts.Select(part => BuildOutputPath(part.PartNumber)).ToArray();
        if (OutputPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != OutputPaths.Count)
        {
            throw new InvalidOperationException("Audio output path collision detected.");
        }
    }

    public IReadOnlyList<AudioSegmentArtifact> Segments { get; }

    public MasteringProfile MasteringProfile { get; }

    public ReleaseAudioFormat OutputFormat { get; }

    public TimeSpan TargetPartDuration { get; }

    public string OutputDirectory { get; }

    public string OutputStem { get; }

    public IReadOnlyList<AudioAssemblyPart> Parts { get; }

    public IReadOnlyList<string> OutputPaths { get; }

    public TimeSpan TotalMeasuredDuration => TimeSpan.FromTicks(Segments.Sum(static segment => segment.MeasuredDuration.Ticks));

    private static IReadOnlyList<AudioAssemblyPart> BuildParts(IReadOnlyList<AudioSegmentArtifact> segments, TimeSpan targetPartDuration)
    {
        var parts = new List<AudioAssemblyPart>();
        var current = new List<AudioSegmentArtifact>();
        var duration = TimeSpan.Zero;

        foreach (var segment in segments)
        {
            if (current.Count > 0 && duration + segment.MeasuredDuration > targetPartDuration)
            {
                parts.Add(new AudioAssemblyPart(parts.Count + 1, current.ToArray(), duration));
                current = [];
                duration = TimeSpan.Zero;
            }

            current.Add(segment);
            duration += segment.MeasuredDuration;
        }

        if (current.Count > 0)
        {
            parts.Add(new AudioAssemblyPart(parts.Count + 1, current.ToArray(), duration));
        }

        return parts;
    }

    private string BuildOutputPath(int partNumber)
    {
        var extension = OutputFormat switch
        {
            ReleaseAudioFormat.Wav => ".wav",
            ReleaseAudioFormat.Mp3 => ".mp3",
            ReleaseAudioFormat.Flac => ".flac",
            ReleaseAudioFormat.M4a => ".m4a",
            _ => throw new InvalidOperationException("Unsupported release audio format."),
        };
        return Path.Combine(OutputDirectory, $"{OutputStem}.part-{partNumber:D3}{extension}");
    }
}
