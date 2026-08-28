namespace CloudScribe.Providers.Abstractions;

public sealed record ProviderVoiceReference
{
    public ProviderVoiceReference(string stableId, string exactProviderVoiceId, string? modelStableId = null)
    {
        StableId = ProviderIdentifierRules.NormalizeStableId(stableId, nameof(stableId), maximumLength: 96);
        ExactProviderVoiceId = ProviderIdentifierRules.NormalizeDisplayName(
            exactProviderVoiceId,
            nameof(exactProviderVoiceId),
            maximumLength: 256);
        ModelStableId = modelStableId is null
            ? null
            : ProviderIdentifierRules.NormalizeStableId(modelStableId, nameof(modelStableId), maximumLength: 96);
    }

    public string StableId { get; }
    public string ExactProviderVoiceId { get; }
    public string? ModelStableId { get; }
}
