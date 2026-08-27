namespace CloudScribe.Domain.Generation;

public sealed record ProviderRouteHealthKey(
    string ProviderStableId,
    string AccountId,
    string OperationStableId,
    string VoiceStableId)
{
    public ProviderRouteHealthKey Validate() => Validate(
        ProviderStableId,
        AccountId,
        OperationStableId,
        VoiceStableId);

    private ProviderRouteHealthKey Validate(
        string providerStableId,
        string accountId,
        string operationStableId,
        string voiceStableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceStableId);
        return this;
    }

    public string StableIdentity => string.Join("/", ProviderStableId, AccountId, OperationStableId, VoiceStableId);
}
