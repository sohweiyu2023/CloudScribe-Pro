namespace CloudScribe.Domain.Generation;

public sealed record ProviderRoute(
    string ProviderStableId,
    string AccountId,
    string OperationStableId,
    string VoiceStableId,
    string PricingProvenanceId,
    long EstimatedMinorUnits,
    string Currency)
{
    public ProviderRoute Validate() => Validate(
        ProviderStableId,
        AccountId,
        OperationStableId,
        VoiceStableId,
        PricingProvenanceId,
        EstimatedMinorUnits,
        Currency);

    private ProviderRoute Validate(
        string providerStableId,
        string accountId,
        string operationStableId,
        string voiceStableId,
        string pricingProvenanceId,
        long estimatedMinorUnits,
        string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedMinorUnits);
        return this;
    }
}
