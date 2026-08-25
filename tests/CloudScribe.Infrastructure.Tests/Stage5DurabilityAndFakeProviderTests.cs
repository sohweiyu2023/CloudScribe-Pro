using System.Text;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage5DurabilityAndFakeProviderTests
{
    [Fact]
    public async Task RecoverySnapshotsSurviveStoreRecreationAndPreserveAmbiguousSubmission()
    {
        var root = CreateScratchDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var jobId = Guid.NewGuid();
            var submission = new GenerationSubmissionRecord(
                "idem-ambiguous",
                SubmissionDisposition.UnknownRequiresReconciliation,
                null,
                1000);
            var snapshot = new GenerationRecoverySnapshot(
                jobId,
                GenerationJobState.SubmissionUnknown,
                1,
                7,
                3,
                submission,
                2000);

            var firstProcess = new AtomicJsonGenerationRecoveryStore(root);
            await firstProcess.SaveAsync(snapshot, cancellationToken).ConfigureAwait(true);

            var restartedProcess = new AtomicJsonGenerationRecoveryStore(root);
            var restored = await restartedProcess.ReadAsync(jobId, cancellationToken).ConfigureAwait(true);
            var recoverable = await restartedProcess.ListRecoverableAsync(cancellationToken).ConfigureAwait(true);

            Assert.NotNull(restored);
            Assert.Equal(GenerationRecoveryKind.Reconcile, restored!.DecideRecovery().Kind);
            Assert.Equal("idem-ambiguous", restored.LastSubmission!.IdempotencyKey);
            Assert.Single(recoverable);
            Assert.Equal(jobId, recoverable[0].JobId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrivateCacheIsReusableByNewProcessInstanceWithSameProtectedKeyNamespace()
    {
        var root = CreateScratchDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var hmacKey = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
            var key = ContentAddressedSegmentKey.FromPrivateLookup(PrivateCacheLookupKey.Derive(
                hmacKey,
                CreateTrustContext(),
                Encoding.UTF8.GetBytes("compiled segment")));
            var expected = Encoding.UTF8.GetBytes("deterministic-media");

            var firstProcess = new FileGenerationSegmentCache(root);
            await firstProcess.StoreAsync(key, expected, cancellationToken).ConfigureAwait(true);

            var restartedProcess = new FileGenerationSegmentCache(root);
            Assert.True(await restartedProcess.ContainsAsync(key, cancellationToken).ConfigureAwait(true));
            Assert.Equal(expected, await restartedProcess.ReadAsync(key, cancellationToken).ConfigureAwait(true));
            Assert.DoesNotContain("compiled segment", string.Join('|', Directory.EnumerateFiles(root)), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptedCachedMediaIsQuarantinedAndNeverReused()
    {
        var root = CreateScratchDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var key = ContentAddressedSegmentKey.FromPrivateLookup(PrivateCacheLookupKey.Derive(
                Enumerable.Repeat((byte)0x44, 32).ToArray(),
                CreateTrustContext(),
                Encoding.UTF8.GetBytes("payload")));
            var cache = new FileGenerationSegmentCache(root);
            await cache.StoreAsync(key, Encoding.UTF8.GetBytes("original-media"), cancellationToken).ConfigureAwait(true);

            var mediaPath = Path.Combine(root, key.PrivateLookupHmacSha256 + ".segment");
            await File.WriteAllBytesAsync(mediaPath, Encoding.UTF8.GetBytes("tampered-media"), cancellationToken).ConfigureAwait(true);

            Assert.Null(await cache.ReadAsync(key, cancellationToken).ConfigureAwait(true));
            Assert.False(File.Exists(mediaPath));
            Assert.True(Directory.Exists(Path.Combine(root, "quarantine")));
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(root, "quarantine")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FakeProviderDeduplicatesSameIdempotencyKeyAndReconcilesSameResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = new DeterministicFakeGenerationProvider();
        var request = new GenerationProviderRequest(
            provider.ProviderStableId,
            "synthesize",
            "account-1",
            "idem-1",
            Encoding.UTF8.GetBytes("payload"),
            "wav");

        var first = await provider.SubmitAsync(request, cancellationToken).ConfigureAwait(true);
        var duplicate = await provider.SubmitAsync(request, cancellationToken).ConfigureAwait(true);
        var reconciled = await provider.ReconcileAsync("idem-1", cancellationToken).ConfigureAwait(true);

        Assert.True(first.IsAccepted);
        Assert.Equal(first.ProviderRequestId, duplicate.ProviderRequestId);
        Assert.Equal(first.MediaBytes.ToArray(), duplicate.MediaBytes.ToArray());
        Assert.NotNull(reconciled);
        Assert.Equal(first.ProviderRequestId, reconciled!.ProviderRequestId);
        Assert.Equal(1, provider.PhysicalSubmissionCount);
    }

    [Fact]
    public async Task FakeUnknownSubmissionRemainsUnknownUntilExplicitReconciliation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = new DeterministicFakeGenerationProvider(FakeGenerationOutcome.SubmissionUnknown);
        var request = new GenerationProviderRequest(
            provider.ProviderStableId,
            "synthesize",
            "account-1",
            "idem-unknown",
            Encoding.UTF8.GetBytes("payload"),
            "wav");

        var response = await provider.SubmitAsync(request, cancellationToken).ConfigureAwait(true);
        var record = new GenerationSubmissionRecord(
            request.IdempotencyKey,
            response.Disposition,
            response.ProviderRequestId,
            1000);
        var recovery = new GenerationRecoverySnapshot(
            Guid.NewGuid(),
            GenerationJobState.SubmissionUnknown,
            1,
            0,
            1,
            record,
            1000);

        Assert.Equal(SubmissionDisposition.UnknownRequiresReconciliation, response.Disposition);
        Assert.Equal(GenerationRecoveryKind.Reconcile, recovery.DecideRecovery().Kind);
        Assert.Equal(1, provider.PhysicalSubmissionCount);
    }

    private static GenerationCacheTrustContext CreateTrustContext() => new(
        "provider-a", "account-a", "project-a", "endpoint-a", "region-a", "synthesize", "model-a",
        "voice-a", "stock-voice-a", "speech-plan-v1", "en-SG", "controls-a", "wav", "pcm16",
        "adapter-v1", "compiler-v1", "ast-v1", "normalize-v1", "pricing-v2.23", "capabilities-a",
        "governance-a", "features-a", "account-capabilities-a");

    private static string CreateScratchDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cloudscribe-stage5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
