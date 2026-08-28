namespace CloudScribe.Providers.Abstractions;

public interface IProviderCapabilitySource
{
    ValueTask<ProviderCapabilitySnapshot> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
}
