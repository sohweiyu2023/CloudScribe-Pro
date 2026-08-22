using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public enum FakeGenerationOutcome
{
    Accept,
    RejectSafeToRetry,
    SubmissionUnknown,
}

public sealed class DeterministicFakeGenerationProvider : IGenerationProvider
{
    private readonly FakeGenerationOutcome _outcome;
    private readonly ConcurrentDictionary<string, GenerationProviderResponse> _responses = new(StringComparer.Ordinal);
    private int _physicalSubmissionCount;

    public DeterministicFakeGenerationProvider(FakeGenerationOutcome outcome = FakeGenerationOutcome.Accept)
    {
        _outcome = outcome;
    }

    public string ProviderStableId => "cloudscribe.fake.deterministic";

    public int PhysicalSubmissionCount => Volatile.Read(ref _physicalSubmissionCount);

    public Task<GenerationProviderResponse> SubmitAsync(GenerationProviderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.ProviderStableId, ProviderStableId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Request targets a different provider.", nameof(request));
        }

        var response = _responses.GetOrAdd(request.IdempotencyKey, _ => CreateResponse(request));
        return Task.FromResult(response);
    }

    public Task<GenerationProviderResponse?> ReconcileAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        cancellationToken.ThrowIfCancellationRequested();
        _responses.TryGetValue(idempotencyKey, out var response);
        return Task.FromResult<GenerationProviderResponse?>(response);
    }

    private GenerationProviderResponse CreateResponse(GenerationProviderRequest request)
    {
        Interlocked.Increment(ref _physicalSubmissionCount);
        return _outcome switch
        {
            FakeGenerationOutcome.Accept => CreateAccepted(request),
            FakeGenerationOutcome.RejectSafeToRetry => new GenerationProviderResponse(
                SubmissionDisposition.RejectedSafeToRetry,
                null,
                ReadOnlyMemory<byte>.Empty,
                null,
                TimeSpan.FromSeconds(2),
                "fake.retryable"),
            FakeGenerationOutcome.SubmissionUnknown => new GenerationProviderResponse(
                SubmissionDisposition.UnknownRequiresReconciliation,
                null,
                ReadOnlyMemory<byte>.Empty,
                null,
                null,
                "fake.submission-unknown"),
            _ => throw new InvalidOperationException("Unknown fake-provider outcome."),
        };
    }

    private static GenerationProviderResponse CreateAccepted(GenerationProviderRequest request)
    {
        var requestIdentity = SHA256.HashData(Encoding.UTF8.GetBytes(request.IdempotencyKey));
        var payloadHash = SHA256.HashData(request.CompiledPayload.Span);
        var media = CreateWave(payloadHash);
        var providerRequestId = "fake-" + Convert.ToHexString(requestIdentity.AsSpan(0, 8)).ToLowerInvariant();
        Array.Clear(requestIdentity);
        Array.Clear(payloadHash);
        return new GenerationProviderResponse(
            SubmissionDisposition.Accepted,
            providerRequestId,
            media,
            "audio/wav",
            null,
            "fake.accepted");
    }

    private static byte[] CreateWave(ReadOnlySpan<byte> seed)
    {
        const int dataLength = 32;
        var media = new byte[44 + dataLength];
        "RIFF"u8.CopyTo(media.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(media.AsSpan(4, 4), (uint)(media.Length - 8));
        "WAVE"u8.CopyTo(media.AsSpan(8, 4));
        "fmt "u8.CopyTo(media.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(media.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(media.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(media.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(media.AsSpan(24, 4), 16000);
        BinaryPrimitives.WriteUInt32LittleEndian(media.AsSpan(28, 4), 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(media.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(media.AsSpan(34, 2), 16);
        "data"u8.CopyTo(media.AsSpan(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(media.AsSpan(40, 4), dataLength);
        seed[..dataLength].CopyTo(media.AsSpan(44));
        return media;
    }
}
