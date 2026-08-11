namespace CloudScribe.Providers.Abstractions;

public interface IProviderAdapterFactory
{
    ProviderDescriptor Descriptor { get; }

    ValueTask<IProviderAdapter> CreateAdapterAsync(
        string accountId,
        CancellationToken cancellationToken = default);
}
