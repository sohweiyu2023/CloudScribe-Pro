using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Tests;

public sealed class Stage5GenerationSegmentExecutorTests
{
    [Fact]
    public async Task CacheHitBypassesProviderSubmission()
    {
        var provider = new RecordingProvider();
        var cache = new MemorySegmentCache();
        var request = CreateRequest();
        var key = ContentAddressedSegmentKey.Create(
            request.CompiledPayload.Span,
            request.ProviderStableId,
            request.OperationStableId,
            request.VoiceStableId,
            request.CompilationProfileId);
        await cache.StoreAsync(key, new byte[] { 1, 2, 3 });

        var result = await new GenerationSegmentExecutor(provider, cache).ExecuteAsync(request);

        Assert.True(result.CacheHit);
        Assert.Equal(0, provider.SubmitCount);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.MediaBytes.ToArray());
        Assert.Equal("segment.cache.hit", result.DiagnosticCode);
    }

    [Fact]
    public async Task AcceptedProviderMediaIsStoredAndSecondExecutionReusesIt()
    {
        var provider = new RecordingProvider
        {
            SubmitResponse = Accepted(new byte[] { 7, 8, 9 }),
        };
        var cache = new MemorySegmentCache();
        var executor = new GenerationSegmentExecutor(provider, cache);
        var request = CreateRequest();

        var first = await executor.ExecuteAsync(request);
        var second = await executor.ExecuteAsync(request);

        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        Assert.Equal(1, provider.SubmitCount);
        Assert.Equal(new byte[] { 7, 8, 9 }, second.MediaBytes.ToArray());
    }

    [Fact]
    public async Task AmbiguousSubmissionIsNeverCachedOrPretendedSuccessful()
    {
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
        var executor = new GenerationSegmentExecutor(provider, cache);

        var result = await executor.ExecuteAsync(CreateRequest());

        Assert.True(result.RequiresReconciliation);
        Assert.False(result.CacheHit);
        Assert.Equal(1, provider.SubmitCount);
        Assert.Equal(0, cache.StoreCount);
    }

    [Fact]
    public async Task ReconciliationCanPersistAcceptedMediaWithoutResubmission()
    {
        var provider = new RecordingProvider
        {
            ReconcileResponse = Accepted(new byte[] { 4, 5, 6 }),
        };
        var cache = new MemorySegmentCache();
        var executor = new GenerationSegmentExecutor(provider, cache);
        var request = CreateRequest();

        var reconciled = await executor.ReconcileAsync(request);
        var cached = await executor.ExecuteAsync(request);

        Assert.NotNull(reconciled);
        Assert.Equal(0, provider.SubmitCount);
        Assert.Equal(1, provider.ReconcileCount);
        Assert.True(cached.CacheHit);
        Assert.Equal(new byte[] { 4, 5, 6 }, cached.MediaBytes.ToArray());
    }

    [Fact]
    public async Task ProviderBindingMismatchFailsBeforeAnyBillableCall()
    {
        var provider = new RecordingProvider();
        var request = CreateRequest() with { ProviderStableId = "other-provider" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new GenerationSegmentExecutor(provider, new MemorySegmentCache()).ExecuteAsync(request));

        Assert.Equal(0, provider.SubmitCount);
    }

    private static GenerationSegmentExecutionRequest CreateRequest() =>
        new(
            "fake-provider",
            "synthesize-speech",
            "account-1",
            "voice-a",
            "compile-v1",
            "idem-1",
            new byte[] { 10, 20, 30 },
            "wav");

    private static GenerationProviderResponse Accepted(byte[] media) =>
        new(
            SubmissionDisposition.Accepted,
            "provider-request-1",
            media,
            "audio/wav",
            null,
            "provider.accepted");

    private sealed class RecordingProvider : IGenerationProvider
    {
        public string ProviderStableId => "fake-provider";

        public int SubmitCount { get; private set; }

        public int ReconcileCount { get; private set; }

        public GenerationProviderResponse SubmitResponse { get; init; } = Accepted(new byte[] { 1 });

        public GenerationProviderResponse? ReconcileResponse { get; init; }

        public Task<GenerationProviderResponse> SubmitAsync(
            GenerationProviderRequest request,
            CancellationToken cancellationToken)
        {
            SubmitCount++;
            return Task.FromResult(SubmitResponse);
        }

        public Task<GenerationProviderResponse?> ReconcileAsync(
            string idempotencyKey,
            CancellationToken cancellationToken)
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
            Task.FromResult(_items.ContainsKey(key.Sha256));

        public Task<byte[]?> ReadAsync(ContentAddressedSegmentKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.TryGetValue(key.Sha256, out var value) ? value.ToArray() : null);

        public Task StoreAsync(
            ContentAddressedSegmentKey key,
            ReadOnlyMemory<byte> mediaBytes,
            CancellationToken cancellationToken = default)
        {
            StoreCount++;
            _items[key.Sha256] = mediaBytes.ToArray();
            return Task.CompletedTask;
        }
    }
}
