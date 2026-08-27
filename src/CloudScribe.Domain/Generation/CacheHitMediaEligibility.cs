namespace CloudScribe.Domain.Generation;

public static class CacheHitMediaEligibility
{
    public static bool IsEligible(
        ReadOnlySpan<byte> cachedMedia,
        string outputFormat,
        CacheReuseMediaMetadata expectedMetadata,
        long durationToleranceMilliseconds = 20)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFormat);
        ArgumentNullException.ThrowIfNull(expectedMetadata);
        expectedMetadata.Validate();

        var validation = ReturnedMediaValidator.Validate(cachedMedia, contentType: null);
        if (!validation.IsValid || validation.DetectedFormat is null)
            return false;

        var expectedFormat = outputFormat.Trim().ToLowerInvariant() switch
        {
            "wav" or "wave" => GenerationAudioFormat.Wav,
            "mp3" or "mpeg" => GenerationAudioFormat.Mp3,
            _ => throw new NotSupportedException($"Cache-hit output format '{outputFormat}' is not supported."),
        };
        if (validation.DetectedFormat.Value != expectedFormat || expectedMetadata.Format != expectedFormat)
            return false;

        if (expectedFormat == GenerationAudioFormat.Wav)
        {
            if (!ReturnedMediaMetadataInspector.TryInspectWav(cachedMedia, out var observed) || observed is null)
                return false;
            return CacheReuseMediaPolicy.IsEligible(observed, expectedMetadata, durationToleranceMilliseconds);
        }

        // MP3 container validation alone is insufficient to prove sample/duration identity.
        // Fail closed until a bounded MP3 metadata inspector is admitted.
        return false;
    }
}
