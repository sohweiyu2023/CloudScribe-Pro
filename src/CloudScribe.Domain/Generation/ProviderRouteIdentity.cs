namespace CloudScribe.Domain.Generation;

public sealed record ProviderRouteIdentity(
    string ProviderId,
    string AccountId,
    string OperationId,
    string VoiceId,
    string PricingProvenanceSha256,
    string CapabilityProvenanceSha256)
{
    public ProviderRouteIdentity Validate()
    {
        return Validate(ProviderId, AccountId, OperationId, VoiceId, PricingProvenanceSha256, CapabilityProvenanceSha256);
    }

    private static ProviderRouteIdentity Validate(
        string providerId,
        string accountId,
        string operationId,
        string voiceId,
        string pricingProvenanceSha256,
        string capabilityProvenanceSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceId);
        ValidateHash(pricingProvenanceSha256, nameof(pricingProvenanceSha256));
        ValidateHash(capabilityProvenanceSha256, nameof(capabilityProvenanceSha256));
        return new ProviderRouteIdentity(providerId, accountId, operationId, voiceId, pricingProvenanceSha256, capabilityProvenanceSha256);
    }

    private static void ValidateHash(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Expected a SHA-256 hexadecimal value.", name);
        }
    }
}
