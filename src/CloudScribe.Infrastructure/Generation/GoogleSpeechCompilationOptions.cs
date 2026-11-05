namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleSpeechCompilationOptions(
    string LanguageCode,
    string VoiceName,
    string AudioEncoding,
    int MaximumPayloadBytes)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GoogleSentenceMarkers? SentenceMarkers { get; init; }

    public GoogleSpeechCompilationOptions Validate()
    {
        var value = Validate(LanguageCode, VoiceName, AudioEncoding, MaximumPayloadBytes);
        if (SentenceMarkers is not { } markers) return value;
        if (markers.Version != 1 || markers.EndScalars is not { Count: >= 1 and <= 32 })
            throw new InvalidOperationException("Sentence marker format is absent, unknown or unbounded.");
        return value with { SentenceMarkers = markers with { EndScalars = markers.EndScalars.ToArray() } };
    }

    private static GoogleSpeechCompilationOptions Validate(
        string languageCode,
        string voiceName,
        string audioEncoding,
        int maximumPayloadBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioEncoding);
        if (maximumPayloadBytes is < 256 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        }

        return new GoogleSpeechCompilationOptions(languageCode, voiceName, audioEncoding, maximumPayloadBytes);
    }
}
