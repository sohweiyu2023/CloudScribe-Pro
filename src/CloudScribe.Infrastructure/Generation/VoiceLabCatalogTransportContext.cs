using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed record VoiceLabCatalogTransportContext(
    VoiceLabCatalogQuery Query,
    string CredentialReferenceId,
    string CapabilityEvidenceId,
    Uri EndpointOrigin);
