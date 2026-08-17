namespace CloudScribe.Providers.Abstractions;

public interface IProviderQuotaSource
{
    ValueTask<IReadOnlyList<ProviderQuotaObservation>> GetQuotaObservationsAsync(
        CancellationToken cancellationToken = default);
}
