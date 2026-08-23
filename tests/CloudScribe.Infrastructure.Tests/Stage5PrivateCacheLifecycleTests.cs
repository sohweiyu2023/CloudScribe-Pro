using System.Security.Cryptography;
using System.Text;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage5PrivateCacheLifecycleTests
{
    [Fact]
    public async Task TrimAsync_EvictsLeastRecentlyUsedUnprotectedEntry()
    {
        var root = CreateScratchDirectory();
        try
        {
            var cache = new FileGenerationSegmentCache(root);
            var first = Key('a');
            var second = Key('b');
            await cache.StoreAsync(first, new byte[] { 1, 2, 3, 4 });
            await Task.Delay(25);
            await cache.StoreAsync(second, new byte[] { 5, 6, 7, 8 });
            await Task.Delay(25);

            Assert.NotNull(await cache.ReadAsync(first));
            var result = await cache.TrimAsync(maximumBytes: 4);

            Assert.Equal(1, result.EntriesEvicted);
            Assert.Equal(4, result.BytesAfter);
            Assert.True(await cache.ContainsAsync(first));
            Assert.False(await cache.ContainsAsync(second));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(GenerationCacheEntryProtection.Active)]
    [InlineData(GenerationCacheEntryProtection.Pinned)]
    [InlineData(GenerationCacheEntryProtection.Referenced)]
    [InlineData(GenerationCacheEntryProtection.UnresolvedSubmission)]
    public async Task TrimAndClear_PreserveProtectedEntries(GenerationCacheEntryProtection protection)
    {
        var root = CreateScratchDirectory();
        try
        {
            var cache = new FileGenerationSegmentCache(root);
            var protectedKey = Key('c');
            var disposableKey = Key('d');
            await cache.StoreAsync(protectedKey, new byte[] { 1, 2, 3, 4 });
            await cache.StoreAsync(disposableKey, new byte[] { 5, 6, 7, 8 });
            await cache.SetProtectionAsync(protectedKey, protection);

            var trimmed = await cache.TrimAsync(maximumBytes: 0);
            Assert.Equal(1, trimmed.EntriesProtected);
            Assert.Equal(1, trimmed.EntriesEvicted);
            Assert.True(await cache.ContainsAsync(protectedKey));
            Assert.False(await cache.ContainsAsync(disposableKey));

            var cleared = await cache.ClearUnprotectedAsync();
            Assert.Equal(0, cleared.EntriesRemoved);
            Assert.Equal(1, cleared.EntriesProtected);
            Assert.True(await cache.ContainsAsync(protectedKey));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClearingProtection_MakesEntryEligibleForExplicitClear()
    {
        var root = CreateScratchDirectory();
        try
        {
            var cache = new FileGenerationSegmentCache(root);
            var key = Key('e');
            await cache.StoreAsync(key, new byte[] { 1, 2, 3, 4 });
            await cache.SetProtectionAsync(key, GenerationCacheEntryProtection.Pinned | GenerationCacheEntryProtection.Referenced);
            Assert.Equal(0, (await cache.ClearUnprotectedAsync()).EntriesRemoved);

            await cache.SetProtectionAsync(key, GenerationCacheEntryProtection.None);
            var cleared = await cache.ClearUnprotectedAsync();

            Assert.Equal(1, cleared.EntriesRemoved);
            Assert.Equal(4, cleared.BytesRemoved);
            Assert.False(await cache.ContainsAsync(key));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PublishedCachePaths_DoNotExposePayloadOrRawPayloadDigest()
    {
        var root = CreateScratchDirectory();
        try
        {
            var payload = Encoding.UTF8.GetBytes("private speech text: customer account 49217");
            var rawPayloadSha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            var trust = new GenerationCacheTrustContext(
                "provider/google",
                "account/test",
                "project/test",
                "https://texttospeech.googleapis.com",
                "global",
                "synthesize-speech",
                "model/immutable-test",
                "voice/en-US/test",
                "voice-fingerprint/test",
                "speech-plan/revision-42",
                "en-US",
                "controls/default",
                "wav",
                "pcm-s16le-16khz-mono",
                "adapter/v2.23",
                "compiler/v2.23",
                "ast/v1",
                "normalizer/v1",
                "pricing/v2.23",
                "capabilities/test",
                "governance/default",
                "provider-features/test",
                "account-capabilities/test");
            var lookup = PrivateCacheLookupKey.Derive(Enumerable.Repeat((byte)0x5A, 32).ToArray(), trust, payload);
            var key = ContentAddressedSegmentKey.FromPrivateLookup(lookup);
            var cache = new FileGenerationSegmentCache(root);

            await cache.StoreAsync(key, new byte[] { 1, 2, 3, 4 });

            var paths = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path))
                .ToArray();
            Assert.NotEmpty(paths);
            Assert.All(paths, path => Assert.DoesNotContain("private speech text", path, StringComparison.OrdinalIgnoreCase));
            Assert.All(paths, path => Assert.DoesNotContain(rawPayloadSha, path, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(paths, path => path.Contains(lookup.HmacSha256, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ContentAddressedSegmentKey Key(char hexCharacter) =>
        new(new string(hexCharacter, 64));

    private static string CreateScratchDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "CloudScribe-v223-cache-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
