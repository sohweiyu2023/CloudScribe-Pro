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
    public ProviderRoute Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(OperationStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(VoiceStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(PricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Currency);
        if (EstimatedMinorUnits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(EstimatedMinorUnits));
        }
        return this;
    }
}
