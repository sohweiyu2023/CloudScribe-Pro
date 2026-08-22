using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed class FileGenerationSegmentCache : IGenerationSegmentCache
{
    private readonly string _directory;

    public FileGenerationSegmentCache(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public Task<bool> ContainsAsync(ContentAddressedSegmentKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(PathFor(key)));
    }

    public async Task<byte[]?> ReadAsync(ContentAddressedSegmentKey key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task StoreAsync(ContentAddressedSegmentKey key, ReadOnlyMemory<byte> mediaBytes, CancellationToken cancellationToken = default)
    {
        if (mediaBytes.IsEmpty)
        {
            throw new ArgumentException("Cached media payload must not be empty.", nameof(mediaBytes));
        }

        var destination = PathFor(key);
        if (File.Exists(destination))
        {
            return;
        }

        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, mediaBytes.ToArray(), cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(temporary, destination, overwrite: false);
            }
            catch (IOException) when (File.Exists(destination))
            {
                // A racing writer won. Content identity is already represented by the key.
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private string PathFor(ContentAddressedSegmentKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Sha256.Length != 64 || key.Sha256.Any(static c => !Uri.IsHexDigit(c)))
        {
            throw new ArgumentException("Segment cache key must be a 64-character SHA-256 hex digest.", nameof(key));
        }

        return Path.Combine(_directory, key.Sha256.ToLowerInvariant() + ".segment");
    }
}
