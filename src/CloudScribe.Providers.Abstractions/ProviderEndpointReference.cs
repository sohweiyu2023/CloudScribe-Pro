namespace CloudScribe.Providers.Abstractions;

public sealed record ProviderEndpointReference
{
    public ProviderEndpointReference(string endpointId, string regionId)
    {
        EndpointId = ProviderIdentifierRules.NormalizeStableId(endpointId, nameof(endpointId), maximumLength: 96);
        RegionId = ProviderIdentifierRules.NormalizeStableId(regionId, nameof(regionId), maximumLength: 96);
    }

    public string EndpointId { get; }
    public string RegionId { get; }
}
