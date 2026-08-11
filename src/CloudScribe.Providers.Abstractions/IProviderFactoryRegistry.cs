namespace CloudScribe.Providers.Abstractions;

public interface IProviderFactoryRegistry
{
    IReadOnlyList<ProviderDescriptor> AvailableProviders { get; }

    bool TryGetFactory(string stableProviderId, out IProviderAdapterFactory? factory);
}
