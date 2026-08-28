namespace CloudScribe.Providers.Abstractions;

public interface IProviderAdapter : IAsyncDisposable
{
    ProviderDescriptor Descriptor { get; }
}
