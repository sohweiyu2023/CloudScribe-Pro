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
    private readonly long _maximumCacheBytes;
    private readonly TimeProvider _timeProvider;

    public FileGenerationSegmentCache(
        string directory,
        long maximumCacheBytes = 1024L * 1024L * 1024L,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (maximumCacheBytes < ReturnedMediaValidator.DefaultMaximumMediaBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumCacheBytes), "Cache capacity must be large enough for at least one maximum-sized media entry.");
        _directory = Path.GetFullPath(directory);
        _quarantineDirectory = Path.Combine(_directory, "quarantine");
        _maximumCacheBytes = maximumCacheBytes;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
            if (!mediaExists && !metadataExists) return null;
            if (!mediaExists || !metadataExists) { QuarantinePair(key); return null; }

            var metadata = await ReadMetadataAsync(metadataPath, cancellationToken).ConfigureAwait(false);
            if (metadata is null || !metadata.IsValidFor(key)) { QuarantinePair(key); return null; }
            var mediaInfo = new FileInfo(mediaPath);
            if (mediaInfo.Length != metadata.LengthBytes || mediaInfo.Length is <= 0 or > ReturnedMediaValidator.DefaultMaximumMediaBytes)
            { QuarantinePair(key); return null; }

            var media = await File.ReadAllBytesAsync(mediaPath, cancellationToken).ConfigureAwait(false);
            var observedHash = SHA256.HashData(media);
            try
            {
                var expectedHash = Convert.FromHexString(metadata.MediaSha256);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(observedHash, expectedHash))
                    { QuarantinePair(key); CryptographicOperations.ZeroMemory(media); return null; }
                }
                finally { CryptographicOperations.ZeroMemory(expectedHash); }
            }
            finally { CryptographicOperations.ZeroMemory(observedHash); }

            var accessed = metadata with { LastAccessedAtUtc = _timeProvider.GetUtcNow() };
            await WriteMetadataAtomicAsync(metadataPath, accessed, cancellationToken).ConfigureAwait(false);
            return media;
        }
        finally { _gate.Release(); }
    }

    public async Task StoreAsync(ContentAddressedSegmentKey key, ReadOnlyMemory<byte> mediaBytes, CancellationToken cancellationToken = default)
    {
        key.Validate();
        if (mediaBytes.IsEmpty || mediaBytes.Length > ReturnedMediaValidator.DefaultMaximumMediaBytes)
            throw new ArgumentException("Cached media payload must be non-empty and within the bounded media size.", nameof(mediaBytes));

        var mediaShaBytes = SHA256.HashData(mediaBytes.Span);
        var mediaSha = Convert.ToHexString(mediaShaBytes).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(mediaShaBytes);
        var now = _timeProvider.GetUtcNow();
        var metadata = new CacheMetadata(MetadataSchema, key.PrivateLookupHmacSha256, mediaSha, mediaBytes.Length, now, now, GenerationCacheEntryProtection.None);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mediaPath = MediaPathFor(key);
            var metadataPath = MetadataPathFor(key);
            if (File.Exists(mediaPath) || File.Exists(metadataPath))
            {
                if (ExistingEntryMatches(key, mediaSha, mediaBytes.Length)) return;
                QuarantinePair(key);
            }

            var token = Guid.NewGuid().ToString("N");
            var temporaryMedia = mediaPath + ".tmp-" + token;
            var temporaryMetadata = metadataPath + ".tmp-" + token;
            try
            {
                FileStream stream = new(temporaryMedia, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(mediaBytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                FileStream metadataStream = new(temporaryMetadata, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await using (metadataStream.ConfigureAwait(false))
                {
                    await JsonSerializer.SerializeAsync(metadataStream, metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
                    await metadataStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                File.Move(temporaryMedia, mediaPath, overwrite: false);
                File.Move(temporaryMetadata, metadataPath, overwrite: false);
            }
            catch
            {
                if (File.Exists(mediaPath) && !File.Exists(metadataPath)) QuarantinePair(key);
                throw;
            }
            finally
            {
                if (File.Exists(temporaryMedia)) File.Delete(temporaryMedia);
                if (File.Exists(temporaryMetadata)) File.Delete(temporaryMetadata);
            }

            TrimCore(_maximumCacheBytes);
        }
        finally { _gate.Release(); }
    }

    public async Task SetProtectionAsync(
        ContentAddressedSegmentKey key,
        GenerationCacheEntryProtection protection,
        CancellationToken cancellationToken = default)
    {
        key.Validate();
        if ((protection & ~(GenerationCacheEntryProtection.Active | GenerationCacheEntryProtection.Pinned |
            GenerationCacheEntryProtection.Referenced | GenerationCacheEntryProtection.UnresolvedSubmission)) != GenerationCacheEntryProtection.None)
            throw new ArgumentOutOfRangeException(nameof(protection));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadataPath = MetadataPathFor(key);
            var metadata = await ReadMetadataAsync(metadataPath, cancellationToken).ConfigureAwait(false);
            if (metadata is null || !metadata.IsValidFor(key) || !File.Exists(MediaPathFor(key)))
                throw new KeyNotFoundException("Cannot protect a cache entry that is absent or invalid.");
            await WriteMetadataAtomicAsync(metadataPath, metadata with { Protection = protection }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<GenerationCacheTrimResult> TrimAsync(long? maximumBytes = null, CancellationToken cancellationToken = default)
    {
        var target = maximumBytes ?? _maximumCacheBytes;
        if (target < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return TrimCore(target); }
        finally { _gate.Release(); }
    }

    public async Task<GenerationCacheClearResult> ClearUnprotectedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = 0;
            var protectedCount = 0;
            long bytesRemoved = 0;
            foreach (var entry in EnumerateValidEntries())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Metadata.Protection != GenerationCacheEntryProtection.None)
                {
                    protectedCount++;
                    continue;
                }

                bytesRemoved += entry.LengthBytes;
                DeletePair(entry);
                removed++;
            }
            return new GenerationCacheClearResult(removed, protectedCount, bytesRemoved);
        }
        finally { _gate.Release(); }
    }

    private GenerationCacheTrimResult TrimCore(long targetBytes)
    {
        var entries = EnumerateValidEntries().ToArray();
        var before = entries.Sum(static entry => entry.LengthBytes);
        var after = before;
        var evicted = 0;
        var protectedCount = entries.Count(static entry => entry.Metadata.Protection != GenerationCacheEntryProtection.None);

        foreach (var entry in entries
            .Where(static entry => entry.Metadata.Protection == GenerationCacheEntryProtection.None)
            .OrderBy(static entry => entry.Metadata.EffectiveLastAccessUtc)
            .ThenBy(static entry => entry.Metadata.CreatedAtUtc))
        {
            if (after <= targetBytes) break;
            DeletePair(entry);
            after -= entry.LengthBytes;
            evicted++;
        }

        return new GenerationCacheTrimResult(before, after, evicted, protectedCount);
    }

    private IEnumerable<CacheEntry> EnumerateValidEntries()
    {
        foreach (var metadataPath in Directory.EnumerateFiles(_directory, "*.metadata.json", SearchOption.TopDirectoryOnly))
        {
            CacheMetadata? metadata;
            try
            {
                if (new FileInfo(metadataPath).Length is <= 0 or > MaximumMetadataBytes) continue;
                metadata = JsonSerializer.Deserialize<CacheMetadata>(File.ReadAllText(metadataPath));
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            { continue; }
            if (metadata is null || !metadata.IsStructurallyValid()) continue;
            var mediaPath = Path.Combine(_directory, metadata.LookupHmacSha256.ToLowerInvariant() + ".segment");
            if (!File.Exists(mediaPath)) continue;
            var length = new FileInfo(mediaPath).Length;
            if (length != metadata.LengthBytes || length <= 0) continue;
            yield return new CacheEntry(mediaPath, metadataPath, length, metadata);
        }
    }

    private bool ExistingEntryMatches(ContentAddressedSegmentKey key, string mediaSha, int length)
    {
        var mediaPath = MediaPathFor(key);
        var metadataPath = MetadataPathFor(key);
        if (!File.Exists(mediaPath) || !File.Exists(metadataPath)) return false;
        try
        {
            if (new FileInfo(metadataPath).Length is <= 0 or > MaximumMetadataBytes) return false;
            var metadata = JsonSerializer.Deserialize<CacheMetadata>(File.ReadAllText(metadataPath));
            if (metadata is null || !metadata.IsValidFor(key) || metadata.LengthBytes != length ||
                !string.Equals(metadata.MediaSha256, mediaSha, StringComparison.OrdinalIgnoreCase) || new FileInfo(mediaPath).Length != length)
                return false;

            var observedHash = SHA256.HashData(File.ReadAllBytes(mediaPath));
            try
            {
                var expectedHash = Convert.FromHexString(metadata.MediaSha256);
                try { return CryptographicOperations.FixedTimeEquals(observedHash, expectedHash); }
                finally { CryptographicOperations.ZeroMemory(expectedHash); }
            }
            finally { CryptographicOperations.ZeroMemory(observedHash); }
        }
        catch (Exception exception) when (exception is JsonException or FormatException or IOException or UnauthorizedAccessException)
        { return false; }
    }

    private async Task<CacheMetadata?> ReadMetadataAsync(string metadataPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(metadataPath)) return null;
        try
        {
            var metadataInfo = new FileInfo(metadataPath);
            if (metadataInfo.Length is <= 0 or > MaximumMetadataBytes) return null;
            FileStream metadataStream = new(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (metadataStream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<CacheMetadata>(metadataStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    private static async Task WriteMetadataAtomicAsync(string metadataPath, CacheMetadata metadata, CancellationToken cancellationToken)
    {
        var temporary = metadataPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, metadataPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void QuarantinePair(ContentAddressedSegmentKey key)
    {
        Directory.CreateDirectory(_quarantineDirectory);
        var token = Guid.NewGuid().ToString("N");
        MoveIfExists(MediaPathFor(key), Path.Combine(_quarantineDirectory, key.PrivateLookupHmacSha256 + "." + token + ".segment"));
        MoveIfExists(MetadataPathFor(key), Path.Combine(_quarantineDirectory, key.PrivateLookupHmacSha256 + "." + token + ".metadata.json"));
    }

    private static void DeletePair(CacheEntry entry)
    {
        if (File.Exists(entry.MediaPath)) File.Delete(entry.MediaPath);
        if (File.Exists(entry.MetadataPath)) File.Delete(entry.MetadataPath);
    }

    private static void MoveIfExists(string source, string destination)
    { if (File.Exists(source)) File.Move(source, destination, overwrite: false); }

    private string MediaPathFor(ContentAddressedSegmentKey key) => Path.Combine(_directory, key.Validate().PrivateLookupHmacSha256.ToLowerInvariant() + ".segment");
    private string MetadataPathFor(ContentAddressedSegmentKey key) => Path.Combine(_directory, key.Validate().PrivateLookupHmacSha256.ToLowerInvariant() + ".metadata.json");

    private sealed record CacheEntry(string MediaPath, string MetadataPath, long LengthBytes, CacheMetadata Metadata);

    private sealed record CacheMetadata(
        string Schema,
        string LookupHmacSha256,
        string MediaSha256,
        long LengthBytes,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset LastAccessedAtUtc = default,
        GenerationCacheEntryProtection Protection = GenerationCacheEntryProtection.None)
    {
        public DateTimeOffset EffectiveLastAccessUtc => LastAccessedAtUtc == default ? CreatedAtUtc : LastAccessedAtUtc;

        public bool IsStructurallyValid() =>
            string.Equals(Schema, MetadataSchema, StringComparison.Ordinal) &&
            LookupHmacSha256.Length == 64 && LookupHmacSha256.All(Uri.IsHexDigit) &&
            MediaSha256.Length == 64 && MediaSha256.All(Uri.IsHexDigit) &&
            LengthBytes > 0 && CreatedAtUtc != default;

        public bool IsValidFor(ContentAddressedSegmentKey key) =>
            IsStructurallyValid() &&
            string.Equals(LookupHmacSha256, key.PrivateLookupHmacSha256, StringComparison.OrdinalIgnoreCase);
    }
}
