namespace CloudScribe.Providers.Abstractions;

public sealed record ProviderGovernanceReference
{
    public ProviderGovernanceReference(string profileId, string provenanceId)
    {
        ProfileId = ProviderIdentifierRules.NormalizeStableId(profileId, nameof(profileId), maximumLength: 96);
        ProvenanceId = ProviderIdentifierRules.NormalizeDisplayName(provenanceId, nameof(provenanceId), maximumLength: 256);
    }

    public string ProfileId { get; }
    public string ProvenanceId { get; }
}
