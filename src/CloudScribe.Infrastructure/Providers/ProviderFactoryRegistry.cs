using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Providers;

public sealed class ProviderFactoryRegistry : IProviderFactoryRegistry
{
    private readonly Dictionary<string, IProviderAdapterFactory> _factories;

    public ProviderFactoryRegistry(IEnumerable<IProviderAdapterFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        Dictionary<string, IProviderAdapterFactory> map = new(StringComparer.OrdinalIgnoreCase);
        List<ProviderDescriptor> descriptors = [];
        foreach (IProviderAdapterFactory factory in factories)
        {
            if (factory is null)
            {
                throw new InvalidOperationException("The provider-factory collection contains a null entry.");
            }
            ProviderDescriptor descriptor = factory.Descriptor
                ?? throw new InvalidOperationException("A provider factory returned a null descriptor.");
            string id = descriptor.StableId;

            if (!map.TryAdd(id, factory))
            {
                throw new InvalidOperationException($"Duplicate provider factory ID: {id}");
            }

            descriptors.Add(descriptor);
        }

        _factories = map;
        AvailableProviders = descriptors
            .OrderBy(descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<ProviderDescriptor> AvailableProviders { get; }

    public bool TryGetFactory(string stableProviderId, out IProviderAdapterFactory? factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableProviderId);
        return _factories.TryGetValue(stableProviderId, out factory);
    }
}
