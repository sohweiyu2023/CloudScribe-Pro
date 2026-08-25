using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Tests;

public sealed class Stage5GenerationSegmentExecutorTests
{
    [Fact]
    public async Task CacheHitBypassesProviderSubmission()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var provider = new RecordingProvider();
        var cache = new MemorySegmentCache();
        var request = CreateRequest();
        var executor = CreateExecutor(provider, cache);
        var key = await executor.CreatePrivateCacheKeyAsync(request, cancellationToken).ConfigureAwait(true);
        await cache.StoreAsync(key, CreateMinimalWav(1), cancellationToken).ConfigureAwait(true);

        var result = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(true);

        Assert.True(result.CacheHit);
        Assert.Equal(0, provider.SubmitCount);
        Assert.Equal("segment.cache.hit", result.DiagnosticCode);
    }

    [Fact]
    public async Task ForceFreshBypassesReusableEntryAndSubmitsAgain()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var provider = new RecordingProvider { SubmitResponse = Accepted(CreateMinimalWav(7)) };
        var cache = new MemorySegmentCache();
        var executor = CreateExecutor(provider, cache);
        var request = CreateRequest();
        var key = await executor.CreatePrivateCacheKeyAsync(request, cancellationToken).ConfigureAwait(true);
        await cache.StoreAsync(key, CreateMinimalWav(1), cancellationToken).ConfigureAwait(true);

        var result = await executor.ExecuteAsync(request with { ForceFresh = true }, cancellationToken).ConfigureAwait(true);

        Assert.False(result.CacheHit);
        Assert.Equal(1, provider.SubmitCount);
        Assert.Equal(CreateMinimalWav(7), result.MediaBytes.ToArray());
    }

    [Fact]
    public async Task CorruptCacheEntryIsIgnoredAndProviderRefreshesIt()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var provider = new RecordingProvider { SubmitResponse = Accepted(CreateMinimalWav(7)) };
        var cache = new MemorySegmentCache();
        var request = CreateRequest();
        var executor = CreateExecutor(provider, cache);
        var key = await executor.CreatePrivateCacheKeyAsync(request, cancellationToken).ConfigureAwait(true);
        await cache.StoreAsync(key, new byte[] { 1, 2, 3 }, cancellationToken).ConfigureAwait(true);

        var result = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(true);

        Assert.False(result.CacheHit);
        Assert.Equal(1, provider.SubmitCount);
        Assert.Equal(CreateMinimalWav(7), result.MediaBytes.ToArray());
    }

    [Fact]
    public async Task AcceptedProviderMediaIsStoredAndSecondExecutionReusesIt()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var media = CreateMinimalWav(7);
        var provider = new RecordingProvider { SubmitResponse = Accepted(media) };
        var cache = new MemorySegmentCache();
        var executor = CreateExecutor(provider, cache);
        var request = CreateRequest();

        var first = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(true);
        var second = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(true);

        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        Assert.Equal(1, provider.SubmitCount);
        Assert.Equal(media, second.MediaBytes.ToArray());
    }

    [Fact]
    public async Task AcceptedCorruptProviderMediaFailsClosedAndIsNeverCached()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var provider = new RecordingProvider { SubmitResponse = Accepted(new byte[] { 7, 8, 9 }) };
        var cache = new MemorySegmentCache();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateExecutor(provider, cache).ExecuteAsync(CreateRequest(), cancellationToken)).ConfigureAwait(true);

        Assert.Equal(1, provider.SubmitCount);
        Assert.Equal(0, cache.StoreCount);
    }

    [Fact]
    public async Task AcceptedWrongFormatProviderMediaFailsClosedAndIsNeverCached()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var provider = new RecordingProvider
        {
            SubmitResponse = new GenerationProviderResponse(
                SubmissionDisposition.Accepted,
                "provider-request-1",
                new byte[] { 0xFF, 0xFB, 0x90, 0x64 },
                "audio/mpeg",
                null,
                "provider.accepted"),
        };
        var cache = new MemorySegmentCache();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateExecutor(provider, cache).ExecuteAsync(CreateRequest(), cancellationToken)).ConfigureAwait(true);

        Assert.Equal(0, cache.StoreCount);
    }

    [Fact]
    public async Task AmbiguousSubmissionIsNeverCachedOrPretendedSuccessful()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var provider = new RecordingProvider
        {
            SubmitResponse = new GenerationProviderResponse(
                SubmissionDisposition.UnknownRequiresReconciliation,
                null,
                ReadOnlyMemory<byte>.Empty,
                null,
                null,
                "provider.submission.unknown"),
        };
        var cache = new MemorySegmentCache();
        var executor = CreateExecutor(provider, cache);

        var result = await executor.ExecuteAsync(CreateRequest(), cancellationToken).ConfigureAwait(true);

        Assert.True(result.RequiresReconciliation);
        Assert.False(result.CacheHit);
        Assert.Equal(1, provider.SubmitCount);
        Assert.Equal(0, cache.StoreCount);
    }

    [Fact]
    public async Task ReconciliationCanPersistAcceptedMediaWithoutResubmission()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var media = CreateMinimalWav(4);
        var provider = new RecordingProvider { ReconcileResponse = Accepted(media) };
        var cache = new MemorySegmentCache();
        var executor = CreateExecutor(provider, cache);
        var request = CreateRequest();

        var reconciled = await executor.ReconcileAsync(request, cancellationToken).ConfigureAwait(true);
        var cached = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(true);

        Assert.NotNull(reconciled);
        Assert.Equal(0, provider.SubmitCount);
        Assert.Equal(1, provider.ReconcileCount);
        Assert.True(cached.CacheHit);
        Assert.Equal(media, cached.MediaBytes.ToArray());
    }

    [Fact]
    public async Task ProviderBindingMismatchFailsBeforeAnyBillableCall()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var provider = new RecordingProvider();
        var request = CreateRequest() with { ProviderStableId = "other-provider" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateExecutor(provider, new MemorySegmentCache()).ExecuteAsync(request, cancellationToken)).ConfigureAwait(true);

        Assert.Equal(0, provider.SubmitCount);
    }

    private static GenerationSegmentExecutor CreateExecutor(IGenerationProvider provider, IGenerationSegmentCache cache) =>
        new(provider, cache, new DeterministicGenerationPrivateCacheKeyProvider("stage5-executor-tests"));

    private static GenerationSegmentExecutionRequest CreateRequest() => new(
        "fake-provider",
        "synthesize-speech",
        "account-1",
        "voice-a",
        "compile-v1",
        "idem-1",
        new byte[] { 10, 20, 30 },
        "wav",
        CreateTrustContext());

    private static GenerationCacheTrustContext CreateTrustContext() => new(
        "fake-provider", "account-1", "project-1", "endpoint-1", "local", "synthesize-speech",
        "model-snapshot-1", "voice-a", "stock-voice-a", "speech-plan-v1", "en-SG", "controls-1", "wav",
        "pcm16", "adapter-v1", "compile-v1", "ast-v1", "normalize-v1", "pricing-v2.23-test",
        "capabilities-v1", "governance-v1", "features-v1", "account-capabilities-v1");

    private static GenerationProviderResponse Accepted(byte[] media) => new(
        SubmissionDisposition.Accepted,
        "provider-request-1",
        media,
        "audio/wav",
        null,
        "provider.accepted");

    private static byte[] CreateMinimalWav(byte sample)
    {
        var bytes = new byte[46];
        "RIFF"u8.CopyTo(bytes);
        BitConverter.GetBytes((uint)38).CopyTo(bytes, 4);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        "fmt "u8.CopyTo(bytes.AsSpan(12));
        BitConverter.GetBytes((uint)16).CopyTo(bytes, 16);
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 20);
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 22);
        BitConverter.GetBytes((uint)8000).CopyTo(bytes, 24);
        BitConverter.GetBytes((uint)8000).CopyTo(bytes, 28);
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 32);
        BitConverter.GetBytes((ushort)8).CopyTo(bytes, 34);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BitConverter.GetBytes((uint)2).CopyTo(bytes, 40);
        bytes[44] = sample;
        bytes[45] = sample;
        return bytes;
    }

    private sealed class RecordingProvider : IGenerationProvider
    {
        public string ProviderStableId => "fake-provider";
        public int SubmitCount { get; private set; }
        public int ReconcileCount { get; private set; }
        public GenerationProviderResponse SubmitResponse { get; init; } = Accepted(CreateMinimalWav(1));
        public GenerationProviderResponse? ReconcileResponse { get; init; }

        public Task<GenerationProviderResponse> SubmitAsync(GenerationProviderRequest request, CancellationToken cancellationToken)
        {
            SubmitCount++;
            return Task.FromResult(SubmitResponse);
        }

        public Task<GenerationProviderResponse?> ReconcileAsync(string idempotencyKey, CancellationToken cancellationToken)
        {
            ReconcileCount++;
            return Task.FromResult(ReconcileResponse);
        }
    }

    private sealed class MemorySegmentCache : IGenerationSegmentCache
    {
        private readonly Dictionary<string, byte[]> _items = new(StringComparer.Ordinal);
        public int StoreCount { get; private set; }

        public Task<bool> ContainsAsync(ContentAddressedSegmentKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.ContainsKey(key.PrivateLookupHmacSha256));

        public Task<byte[]?> ReadAsync(ContentAddressedSegmentKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.TryGetValue(key.PrivateLookupHmacSha256, out var value) ? value.ToArray() : null);

        public Task StoreAsync(ContentAddressedSegmentKey key, ReadOnlyMemory<byte> mediaBytes, CancellationToken cancellationToken = default)
        {
            StoreCount++;
            _items[key.PrivateLookupHmacSha256] = mediaBytes.ToArray();
            return Task.CompletedTask;
        }
    }
}
