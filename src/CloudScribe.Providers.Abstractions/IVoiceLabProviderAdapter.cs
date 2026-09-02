namespace CloudScribe.Providers.Abstractions;

/// <summary>
/// Exposes provider-native Voice Lab operations through an explicitly resolved provider adapter.
/// Consumers must resolve this capability through the provider-factory registry rather than
/// treating every generic provider adapter as Voice Lab capable.
/// </summary>
public interface IVoiceLabProviderAdapter : IProviderAdapter
{
    Task<IReadOnlyList<VoiceLabProviderCatalogVoice>> QueryVoiceLabCatalogAsync(
        VoiceLabProviderCatalogRequest request,
        CancellationToken cancellationToken = default);
}
