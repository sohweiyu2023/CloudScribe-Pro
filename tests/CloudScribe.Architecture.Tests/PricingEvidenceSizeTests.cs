using System.Net;
using System.Security.Cryptography;
using CloudScribe.App.Composition;
using CloudScribe.Application.Pricing;
using CloudScribe.Domain.Pricing;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.Architecture.Tests;

public sealed class PricingEvidenceSizeTests
{
    [Fact]
    public async Task ConfirmedObservationCompletesUnsignedActivationWithBoundedProvenance()
    {
        using var content = new ByteArrayContent(new byte[2300000]);
        using var handler = new PricingHandler(content);
        using var client = new HttpClient(handler);
        var history = new RecordingHistory();
        var service = new BuiltInPricingCatalogBootstrapService(new V222ControlSet(new StrictJsonObjectReader()), new ValidAdmission(), history, client);
        var source = new Uri("https://cloud.google.com/text-to-speech/pricing?hl=en");

        var result = await service.VerifyCurrentSourceAndActivateAsync(source, true, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(PricingCatalogTrustState.ValidUnsigned, result.Snapshot.TrustState);
        Assert.InRange(result.Snapshot.Source.Label.Length, 1, 240);
        Assert.Contains(source.AbsoluteUri, result.Snapshot.Source.Label, StringComparison.Ordinal);
        Assert.Contains(result.Observation.Sha256, result.Snapshot.Source.Label, StringComparison.Ordinal);
        Assert.Contains("unsigned", result.Snapshot.Source.Label, StringComparison.Ordinal);
        Assert.Equal(PricingCatalogApprovalKind.ManualUnsigned, history.LastActivation!.ApprovalKind);
        Assert.True(history.LastActivation.UserConfirmed);
    }

    [Theory]
    [InlineData(2300000)]
    [InlineData(4 * 1024 * 1024)]
    public async Task BoundedCurrentPricingPageReachesCatalogAdmission(int byteCount)
    {
        using var content = new ByteArrayContent(new byte[byteCount]);
        using var handler = new PricingHandler(content);
        using var client = new HttpClient(handler);
        var admission = new StopAtAdmission();
        var service = CreateService(client, admission);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifyCurrentSourceAndActivateAsync(new Uri("https://cloud.google.com/text-to-speech/pricing"), true, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal("Synthetic admission boundary reached.", error.Message);
        Assert.Equal(1, admission.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedDeclaredOrStreamingPageFailsBeforeAdmission(bool declaredLength)
    {
        byte[] bytes = new byte[4 * 1024 * 1024 + 1];
        using HttpContent content = declaredLength ? new ByteArrayContent(bytes) : new UnknownLengthContent(bytes);
        using var handler = new PricingHandler(content);
        using var client = new HttpClient(handler);
        var admission = new StopAtAdmission();
        var service = CreateService(client, admission);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyCurrentSourceAndActivateAsync(new Uri("https://cloud.google.com/text-to-speech/pricing"), true, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("4 MiB safety limit", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, admission.Calls);
    }

    [Fact]
    public async Task EmptyPageStillFailsBeforeAdmission()
    {
        using var content = new ByteArrayContent([]);
        using var handler = new PricingHandler(content);
        using var client = new HttpClient(handler);
        var admission = new StopAtAdmission();
        var service = CreateService(client, admission);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyCurrentSourceAndActivateAsync(new Uri("https://cloud.google.com/text-to-speech/pricing"), true, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("empty response", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, admission.Calls);
    }

    private static BuiltInPricingCatalogBootstrapService CreateService(HttpClient client, StopAtAdmission admission) => new(new V222ControlSet(new StrictJsonObjectReader()), admission, new UnusedHistory(), client);

    private sealed class StopAtAdmission : IPricingCatalogAdmissionService
    {
        public int Calls { get; private set; }

        public PricingCatalogDryRunResult DryRun(ReadOnlyMemory<byte> utf8Catalog, PricingCatalogSignature? signature = null)
        {
            Calls++;
            throw new InvalidDataException("Synthetic admission boundary reached.");
        }
    }

    private sealed class ValidAdmission : IPricingCatalogAdmissionService
    {
        public PricingCatalogDryRunResult DryRun(ReadOnlyMemory<byte> utf8Catalog, PricingCatalogSignature? signature = null) => new(PricingCatalogTrustState.ValidUnsigned, [], "Synthetic valid unsigned admission.");
    }

    private sealed class RecordingHistory : IPricingCatalogHistoryStore
    {
        private PricingCatalogSnapshot? _snapshot;
        private PricingCatalogSnapshot? _active;
        public PricingCatalogActivationRequest? LastActivation { get; private set; }

        public Task<PricingCatalogSnapshot> SaveSnapshotAsync(ReadOnlyMemory<byte> utf8Catalog, PricingCatalogTrustState trustState, PricingCatalogSource source, string? signatureKeyId = null, CancellationToken cancellationToken = default)
        {
            _snapshot = new PricingCatalogSnapshot(Guid.NewGuid(), Convert.ToHexString(SHA256.HashData(utf8Catalog.Span)).ToLowerInvariant(), utf8Catalog.Length, trustState, source, DateTimeOffset.UtcNow, signatureKeyId);
            return Task.FromResult(_snapshot);
        }

        public Task<IReadOnlyList<PricingCatalogSnapshot>> ListSnapshotsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PricingCatalogSnapshot>>(_snapshot is null ? [] : [_snapshot]);
        public Task<PricingCatalogSnapshot?> GetActiveSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(_active);
        public Task<IReadOnlyList<PricingCatalogActivation>> ListActivationsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PricingCatalogActivation>>([]);

        public Task<PricingCatalogActivation> ActivateAsync(PricingCatalogActivationRequest request, CancellationToken cancellationToken = default)
        {
            LastActivation = request;
            _active = _snapshot;
            return Task.FromResult(new PricingCatalogActivation(1, request.SnapshotId, null, request.Kind, request.ApprovalKind, request.Reason, DateTimeOffset.UtcNow));
        }
    }

    private sealed class UnusedHistory : IPricingCatalogHistoryStore
    {
        public Task<PricingCatalogSnapshot> SaveSnapshotAsync(ReadOnlyMemory<byte> utf8Catalog, PricingCatalogTrustState trustState, PricingCatalogSource source, string? signatureKeyId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PricingCatalogSnapshot>> ListSnapshotsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PricingCatalogSnapshot?> GetActiveSnapshotAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PricingCatalogActivation>> ListActivationsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PricingCatalogActivation> ActivateAsync(PricingCatalogActivationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PricingHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes.AsMemory()).AsTask();
    }
}
