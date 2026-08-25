namespace CloudScribe.Domain.Generation;

public sealed record GenerationCircuitBreakerKey(
    string ProviderStableId,
    string AccountId,
    string EndpointId,
    string RegionId,
    string OperationStableId)
{
    public GenerationCircuitBreakerKey Validate()
    {
        return Validate(ProviderStableId, AccountId, EndpointId, RegionId, OperationStableId);
    }

    private static GenerationCircuitBreakerKey Validate(
        string providerStableId,
        string accountId,
        string endpointId,
        string regionId,
        string operationStableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(regionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationStableId);
        return new GenerationCircuitBreakerKey(providerStableId, accountId, endpointId, regionId, operationStableId);
    }
}
