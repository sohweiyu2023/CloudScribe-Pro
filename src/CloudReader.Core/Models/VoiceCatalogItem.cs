namespace CloudReader.Core.Models;

public sealed record VoiceCatalogItem(
    string Name,
    IReadOnlyList<string> LanguageCodes,
    string SsmlGender,
    int NaturalSampleRateHertz,
    string Tier);
