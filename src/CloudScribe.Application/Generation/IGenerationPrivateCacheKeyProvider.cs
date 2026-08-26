namespace CloudScribe.Application.Generation;

public interface IGenerationPrivateCacheKeyProvider
{
    ValueTask<GenerationPrivateCacheKeyMaterial> GetOrCreateAsync(CancellationToken cancellationToken = default);
}
