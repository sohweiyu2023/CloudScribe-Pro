namespace CloudScribe.Domain.Generation;

public sealed record VoiceLabQuery(
    string? Text,
    string? ProviderStableId,
    string? Language,
    IReadOnlySet<string> RequiredCapabilities,
    bool RequireAudition,
    bool ExcludeStaleCapabilities = true);
