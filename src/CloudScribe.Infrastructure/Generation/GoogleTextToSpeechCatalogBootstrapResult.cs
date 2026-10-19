using CloudScribe.Application.Providers;

namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleTextToSpeechCatalogBootstrapResult(
    ProviderAccountSnapshot Account,
    StoredProviderCapabilitySnapshot CapabilityEvidence,
    string ProjectId,
    string CatalogProvenanceId,
    IReadOnlyList<GoogleVoiceCatalogEntry> Voices,
    DateTimeOffset ExpiresAtUtc);
