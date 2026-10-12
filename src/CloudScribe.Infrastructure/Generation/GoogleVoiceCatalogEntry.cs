namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleVoiceCatalogEntry(
    string Name,
    IReadOnlyList<string> LanguageCodes,
    string SsmlGender,
    int NaturalSampleRateHertz);
