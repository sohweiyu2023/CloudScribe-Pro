using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleVoiceCatalogSnapshot(
    ProviderAccountReference Account,
    DateTimeOffset ObservedAtUtc,
    Uri Endpoint,
    string ProvenanceId,
    IReadOnlyList<GoogleVoiceCatalogEntry> Voices)
{
    public IReadOnlySet<string> VoiceNames => Voices.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
    public IReadOnlySet<string> LanguageCodes => Voices
        .SelectMany(item => item.LanguageCodes)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
