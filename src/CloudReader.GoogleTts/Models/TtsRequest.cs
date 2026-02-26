namespace CloudReader.GoogleTts.Models;

public sealed record TtsRequest(
    string Input,
    string VoiceName,
    string LanguageCode,
    string AudioEncoding,
    double SpeakingRate,
    double Pitch,
    double VolumeGainDb,
    int? SampleRateHertz,
    bool IsSsml = false);
