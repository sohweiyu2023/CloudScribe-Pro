namespace CloudScribe.Providers.Abstractions;

public interface IGenerationProvider
{
    string ProviderStableId { get; }

    Task<GenerationProviderResponse> SubmitAsync(GenerationProviderRequest request, CancellationToken cancellationToken);

    Task<GenerationProviderResponse?> ReconcileAsync(string idempotencyKey, CancellationToken cancellationToken);
}
