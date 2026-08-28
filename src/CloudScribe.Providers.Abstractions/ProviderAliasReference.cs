namespace CloudScribe.Providers.Abstractions;

public sealed record ProviderAliasReference
{
    public ProviderAliasReference(string alias, string targetStableId, string provenanceId)
    {
        Alias = ProviderIdentifierRules.NormalizeDisplayName(alias, nameof(alias), maximumLength: 256);
        TargetStableId = ProviderIdentifierRules.NormalizeStableId(targetStableId, nameof(targetStableId), maximumLength: 96);
        ProvenanceId = ProviderIdentifierRules.NormalizeDisplayName(provenanceId, nameof(provenanceId), maximumLength: 256);
    }

    public string Alias { get; }
    public string TargetStableId { get; }
    public string ProvenanceId { get; }
}
