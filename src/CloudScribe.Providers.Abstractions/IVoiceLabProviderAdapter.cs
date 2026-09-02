namespace CloudScribe.Providers.Abstractions;

/// <summary>
/// Identifies provider adapters that explicitly expose the Voice Lab production capability.
/// Consumers must resolve this capability through the provider-factory registry rather than
/// treating every generic provider adapter as Voice Lab capable.
/// </summary>
public interface IVoiceLabProviderAdapter : IProviderAdapter
{
}
