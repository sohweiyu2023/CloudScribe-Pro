using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed class DeterministicGenerationPrivateCacheKeyProvider : IGenerationPrivateCacheKeyProvider
{
    private readonly byte[] _key;

    public DeterministicGenerationPrivateCacheKeyProvider(string seed = "cloudscribe-stage5-deterministic-private-cache-key")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seed);
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
    }

    public ValueTask<GenerationPrivateCacheKeyMaterial> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new GenerationPrivateCacheKeyMaterial(_key.ToArray()));
    }
}
