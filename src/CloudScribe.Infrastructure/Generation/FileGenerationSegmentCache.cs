using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed class FileGenerationSegmentCache : IGenerationSegmentCache
{
    private const string MetadataSchema = "cloudscribe.private-segment-cache.v2.23";
    private const int MaximumMetadataBytes = 4096;
    private readonly string _directory;
    private readonly string _quarantineDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileGenerationSegmentCache(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _quarantineDirectory = Path.Combine(_directory, "quarantine");
        Directory.CreateDirectory(_directory);
    }

    public async Task<bool> ContainsAsync(ContentAddressedSegmentKey key, CancellationToken cancellationToken = default) =>
        await ReadAsync(key, cancellationToken).ConfigureAwait(false) is { Length: > 0 };

    public async Task<byte[]?> ReadAsync(ContentAddressedSegmentKey key, CancellationToken cancellationToken = default)
    {
        key.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mediaPath = MediaPathFor(key);
            var metadataPath = MetadataPathFor(key);
            var mediaExists = File.Exists(mediaPath);
            var metadataExists = File.Exists(metadataPath);
            if (!mediaExists && !metadataExists)
            {
                return null;
            }

            if (!mediaExists || !metadataExists)
            {
                QuarantinePair(key);
                return null;
            }

            CacheMetadata metadata;
            try
            {
                var metadataInfo = new FileInfo(metadataPath);
                if (metadataInfo.Length is <= 0 or > MaximumMetadataBytes)
                {
                    QuarantinePair(key);
                    return null;
                }

                await using var metadataStream = new FileStream(
                    metadataPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                metadata = await JsonSerializer.DeserializeAsync<CacheMetadata>(metadataStream, cancellationToken: cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Private cache metadata is empty.");
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                QuarantinePair(key);
                return null;
            }

            if (!metadata.IsValidFor(key))
            {
                QuarantinePair(key);
                return null;
            }

            var mediaInfo = new FileInfo(mediaPath);
            if (mediaInfo.Length != metadata.LengthBytes ||
                mediaInfo.Length is <= 0 or > ReturnedMediaValidator.DefaultMaximumMediaBytes)
            {
                QuarantinePair(key);
                return null;
            }

            var media = await File.ReadAllBytesAsync(mediaPath, cancellationToken).ConfigureAwait(false);
            var observedHash = SHA256.HashData(media);
            try
            {
                var expectedHash = Convert.FromHexString(metadata.MediaSha256);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(observedHash, expectedHash))
                    {
                        QuarantinePair(key);
                        CryptographicOperations.ZeroMemory(media);
                        return null;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(expectedHash);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(observedHash);
            }

            return media;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StoreAsync(ContentAddressedSegmentKey key, ReadOnlyMemory<byte> mediaBytes, CancellationToken cancellationToken = default)
    {
        key.Validate();
        if (mediaBytes.IsEmpty || mediaBytes.Length > ReturnedMediaValidator.DefaultMaximumMediaBytes)
        {
            throw new ArgumentException("Cached media payload must be non-empty and within the bounded media size.", nameof(mediaBytes));
        }

        var mediaShaBytes = SHA256.HashData(mediaBytes.Span);
        var mediaSha = Convert.ToHexString(mediaShaBytes).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(mediaShaBytes);
        var metadata = new CacheMetadata(
            MetadataSchema,
            key.PrivateLookupHmacSha256,
            mediaSha,
            mediaBytes.Length,
            DateTimeOffset.UtcNow);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mediaPath = MediaPathFor(key);
            var metadataPath = MetadataPathFor(key);
            if (File.Exists(mediaPath) || File.Exists(metadataPath))
            {
                if (ExistingEntryMatches(key, mediaSha, mediaBytes.Length))
                {
                    return;
                }

                QuarantinePair(key);
            }

            var token = Guid.NewGuid().ToString("N");
            var temporaryMedia = mediaPath + ".tmp-" + token;
            var temporaryMetadata = metadataPath + ".tmp-" + token;
            try
            {
                await using (var stream = new FileStream(
                    temporaryMedia,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(mediaBytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var metadataStream = new FileStream(
                    temporaryMetadata,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(metadataStream, metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
                    await metadataStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryMedia, mediaPath, overwrite: false);
                File.Move(temporaryMetadata, metadataPath, overwrite: false); // metadata-last makes incomplete publication non-reusable.
            }
            catch
            {
                if (File.Exists(mediaPath) && !File.Exists(metadataPath))
                {
                    QuarantinePair(key);
                }
                throw;
            }
            finally
            {
                if (File.Exists(temporaryMedia)) File.Delete(temporaryMedia);
                if (File.Exists(temporaryMetadata)) File.Delete(temporaryMetadata);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool ExistingEntryMatches(ContentAddressedSegmentKey key, string mediaSha, int length)
    {
        var mediaPath = MediaPathFor(key);
        var metadataPath = MetadataPathFor(key);
        if (!File.Exists(mediaPath) || !File.Exists(metadataPath))
        {
            return false;
        }

        try
        {
            if (new FileInfo(metadataPath).Length is <= 0 or > MaximumMetadataBytes)
            {
                return false;
            }

            var metadata = JsonSerializer.Deserialize<CacheMetadata>(File.ReadAllText(metadataPath));
            return metadata is not null &&
                metadata.IsValidFor(key) &&
                metadata.LengthBytes == length &&
                string.Equals(metadata.MediaSha256, mediaSha, StringComparison.OrdinalIgnoreCase) &&
                new FileInfo(mediaPath).Length == length;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void QuarantinePair(ContentAddressedSegmentKey key)
    {
        Directory.CreateDirectory(_quarantineDirectory);
        var token = Guid.NewGuid().ToString("N");
        MoveIfExists(MediaPathFor(key), Path.Combine(_quarantineDirectory, key.PrivateLookupHmacSha256 + "." + token + ".segment"));
        MoveIfExists(MetadataPathFor(key), Path.Combine(_quarantineDirectory, key.PrivateLookupHmacSha256 + "." + token + ".metadata.json"));
    }

    private static void MoveIfExists(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Move(source, destination, overwrite: false);
        }
    }

    private string MediaPathFor(ContentAddressedSegmentKey key) =>
        Path.Combine(_directory, key.Validate().PrivateLookupHmacSha256.ToLowerInvariant() + ".segment");

    private string MetadataPathFor(ContentAddressedSegmentKey key) =>
        Path.Combine(_directory, key.Validate().PrivateLookupHmacSha256.ToLowerInvariant() + ".metadata.json");

    private sealed record CacheMetadata(
        string Schema,
        string LookupHmacSha256,
        string MediaSha256,
        long LengthBytes,
        DateTimeOffset CreatedAtUtc)
    {
        public bool IsValidFor(ContentAddressedSegmentKey key) =>
            string.Equals(Schema, MetadataSchema, StringComparison.Ordinal) &&
            string.Equals(LookupHmacSha256, key.PrivateLookupHmacSha256, StringComparison.OrdinalIgnoreCase) &&
            MediaSha256.Length == 64 && MediaSha256.All(Uri.IsHexDigit) &&
            LengthBytes > 0 &&
            CreatedAtUtc != default;
    }
}
