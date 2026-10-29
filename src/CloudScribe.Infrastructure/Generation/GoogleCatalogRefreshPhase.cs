namespace CloudScribe.Infrastructure.Generation;

public enum GoogleCatalogRefreshPhase
{
    AccountLookup, CredentialIdentity, CatalogEndpoint, SynthesisEndpoint, EndpointBinding,
    CatalogRequest, CatalogToken, CatalogRead, CatalogParse,
    OAuthTokenRequest, OAuthTokenRead,
    CapabilityProbe, ProbeToken, ProbeRequest, ProbeRead, ProbeValidation,
    AccountRevalidation, EvidencePersistence, EvidenceWindow, CapabilitySave, ProjectSave,
    VoiceAuthorizationsSave, Complete, UiBootstrap, UiQuery, UiAccountLookup,
}
