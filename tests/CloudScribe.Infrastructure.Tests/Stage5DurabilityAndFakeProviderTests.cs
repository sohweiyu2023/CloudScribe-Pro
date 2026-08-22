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
            await firstProcess.SaveAsync(snapshot);

            var restartedProcess = new AtomicJsonGenerationRecoveryStore(root);
            var restored = await restartedProcess.ReadAsync(jobId);
            var recoverable = await restartedProcess.ListRecoverableAsync();

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
    public async Task ContentAddressedCacheIsReusableByNewProcessInstance()
    {
        var root = CreateScratchDirectory();
        try
        {
            var key = ContentAddressedSegmentKey.Create(
                Encoding.UTF8.GetBytes("compiled segment"),
                "provider-a",
                "synthesize",
                "voice-a",
                "profile-v1");
            var expected = Encoding.UTF8.GetBytes("deterministic-media");

            var firstProcess = new FileGenerationSegmentCache(root);
            await firstProcess.StoreAsync(key, expected);

            var restartedProcess = new FileGenerationSegmentCache(root);
            Assert.True(await restartedProcess.ContainsAsync(key));
            Assert.Equal(expected, await restartedProcess.ReadAsync(key));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FakeProviderDeduplicatesSameIdempotencyKeyAndReconcilesSameResult()
    {
        var provider = new DeterministicFakeGenerationProvider();
        var request = new GenerationProviderRequest(
            provider.ProviderStableId,
            "synthesize",
            "account-1",
            "idem-1",
            Encoding.UTF8.GetBytes("payload"),
            "wav");

        var first = await provider.SubmitAsync(request, CancellationToken.None);
        var duplicate = await provider.SubmitAsync(request, CancellationToken.None);
        var reconciled = await provider.ReconcileAsync("idem-1", CancellationToken.None);

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
        var provider = new DeterministicFakeGenerationProvider(FakeGenerationOutcome.SubmissionUnknown);
        var request = new GenerationProviderRequest(
            provider.ProviderStableId,
            "synthesize",
            "account-1",
            "idem-unknown",
            Encoding.UTF8.GetBytes("payload"),
            "wav");

        var response = await provider.SubmitAsync(request, CancellationToken.None);
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

    private static string CreateScratchDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cloudscribe-stage5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
