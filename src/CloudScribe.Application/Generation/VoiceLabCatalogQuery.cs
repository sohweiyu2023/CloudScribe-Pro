namespace CloudScribe.Application.Generation;

public sealed record VoiceLabCatalogQuery(
    string ProviderId,
    string AccountId,
    string ProjectId,
    string? SearchText,
    string? Locale,
    bool IncludePrivateVoices);
