namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleSpeechCompilationOptions(
    string LanguageCode,
    string VoiceName,
    string AudioEncoding,
    int MaximumPayloadBytes)
{
    public GoogleSpeechCompilationOptions Validate()
    {
        return Validate(LanguageCode, VoiceName, AudioEncoding, MaximumPayloadBytes);
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
