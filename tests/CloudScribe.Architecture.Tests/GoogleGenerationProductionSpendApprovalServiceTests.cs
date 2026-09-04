using System.Security.Cryptography;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleGenerationProductionSpendApprovalServiceTests
{
    [Fact]
    public async Task ApproveExplicitAsyncWithoutUserConfirmationPreservesPendingStateAndSkipsPersistence()
    {
        var store = new RecordingAuthorizationStore();
        var pendingOwner = new GoogleGenerationProductionPendingApprovalStateOwner();
        var owner = new GoogleGenerationProductionSubmissionStateOwner(store);
        var service = new GoogleGenerationProductionSpendApprovalService(pendingOwner, owner);
        pendingOwner.Publish(CreatePendingState(currentEstimateMinorUnits: 125));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApproveExplicitAsync(
                new GoogleGenerationProductionSpendApprovalService.ApprovalConfirmation(150, false),
                CancellationToken.None));

        Assert.Contains("explicit user confirmation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.SaveCount);
        Assert.NotNull(await pendingOwner.ResolveCurrentAsync(CancellationToken.None));
        Assert.Null(await owner.ResolveCurrentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ApproveExplicitAsyncEstimateAboveAuthorizedCeilingPreservesPendingStateAndSkipsPersistence()
    {
        var store = new RecordingAuthorizationStore();
        var pendingOwner = new GoogleGenerationProductionPendingApprovalStateOwner();
        var owner = new GoogleGenerationProductionSubmissionStateOwner(store);
        var service = new GoogleGenerationProductionSpendApprovalService(pendingOwner, owner);
        pendingOwner.Publish(CreatePendingState(currentEstimateMinorUnits: 151));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApproveExplicitAsync(
                new GoogleGenerationProductionSpendApprovalService.ApprovalConfirmation(150, true),
                CancellationToken.None));

        Assert.Contains("estimate exceeds", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.SaveCount);
        Assert.NotNull(await pendingOwner.ResolveCurrentAsync(CancellationToken.None));
        Assert.Null(await owner.ResolveCurrentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ApproveExplicitAsyncWithoutCurrentPendingSubmissionFailsClosed()
    {
        var store = new RecordingAuthorizationStore();
        var pendingOwner = new GoogleGenerationProductionPendingApprovalStateOwner();
        var owner = new GoogleGenerationProductionSubmissionStateOwner(store);
        var service = new GoogleGenerationProductionSpendApprovalService(pendingOwner, owner);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApproveExplicitAsync(
                new GoogleGenerationProductionSpendApprovalService.ApprovalConfirmation(150, true),
                CancellationToken.None));

        Assert.Contains("no current compiled submission", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.SaveCount);
        Assert.Null(await owner.ResolveCurrentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ApproveExplicitAsyncConsumesExactPendingSubmissionOnlyAfterDurableApproval()
    {
        var store = new RecordingAuthorizationStore();
        var pendingOwner = new GoogleGenerationProductionPendingApprovalStateOwner();
        var owner = new GoogleGenerationProductionSubmissionStateOwner(store);
        var service = new GoogleGenerationProductionSpendApprovalService(pendingOwner, owner);
        GoogleGenerationProductionPendingApprovalStateOwner.PendingState pending =
            CreatePendingState(currentEstimateMinorUnits: 125);
        pendingOwner.Publish(pending);

        await service.ApproveExplicitAsync(
            new GoogleGenerationProductionSpendApprovalService.ApprovalConfirmation(150, true),
            CancellationToken.None);

        Assert.Equal(1, store.SaveCount);
        Assert.Null(await pendingOwner.ResolveCurrentAsync(CancellationToken.None));
        GoogleGenerationProductionSubmissionState? approved = await owner.ResolveCurrentAsync(CancellationToken.None);
        Assert.NotNull(approved);
        Assert.Equal(pending.Envelope, approved.Envelope);
        Assert.Same(pending.Snapshot, approved.Snapshot);
        Assert.Equal(125, approved.CurrentEstimateMinorUnits);
    }

    [Fact]
    public void PublishWithCompiledPayloadDigestDriftFailsBeforeBecomingApprovable()
    {
        var pendingOwner = new GoogleGenerationProductionPendingApprovalStateOwner();
        GoogleGenerationProductionPendingApprovalStateOwner.PendingState pending =
            CreatePendingState(currentEstimateMinorUnits: 125);
        GenerationProviderRequest driftedProviderRequest = new(
            GoogleGenerationProvider.StableProviderId,
            GoogleGenerationProvider.SynthesizeOperationStableId,
            "account-1",
            "idempotency-1",
            "different-payload"u8.ToArray(),
            "MP3");
        pending = pending with
        {
            Snapshot = pending.Snapshot with { ProviderRequest = driftedProviderRequest },
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => pendingOwner.Publish(pending));

        Assert.Contains("compiled payload", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(pendingOwner.ResolveCurrentAsync(CancellationToken.None).GetAwaiter().GetResult());
    }

    private static GoogleGenerationProductionPendingApprovalStateOwner.PendingState CreatePendingState(
        long currentEstimateMinorUnits)
    {
        byte[] payload = "provider-payload"u8.ToArray();
        GoogleGenerationSubmissionEnvelope envelope = new(
            "account-1",
            "credential-1",
            "capability-1",
            "pricing-1",
            7,
            "voice-1",
            "MP3",
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            payload.Length);
        GenerationProviderRequest providerRequest = new(
            GoogleGenerationProvider.StableProviderId,
            GoogleGenerationProvider.SynthesizeOperationStableId,
            "account-1",
            "idempotency-1",
            payload,
            "MP3");
        GoogleGenerationPersistedQueueState queueState = new(
            "account-1",
            GoogleGenerationProvider.SynthesizeOperationStableId,
            "idempotency-1",
            false,
            null);
        GenerationCacheTrustContext trust = new(
            GoogleGenerationProvider.StableProviderId,
            "account-1",
            "project-1",
            "endpoint-1",
            "region-1",
            GoogleGenerationProvider.SynthesizeOperationStableId,
            "model-1",
            "voice-1",
            "voice-fingerprint-1",
            "speech-plan-1",
            "en-US",
            "controls-1",
            "MP3",
            "sample-format-1",
            "adapter-1",
            "compiler-1",
            "ast-1",
            "normalization-1",
            "pricing-1",
            "capability-1",
            "governance-1",
            "provider-feature-1",
            "account-capability-1");
        GoogleGenerationUiExecutionSnapshot snapshot = new(
            new GoogleGenerationUiSelection("account-1", "project-1", "voice-1", "model-1", "capability-1", "MP3"),
            true,
            true,
            true,
            true,
            providerRequest,
            trust,
            queueState,
            queueState,
            GoogleGenerationReconciliationResolutionEvidence.None,
            true,
            true,
            true,
            true);

        return new GoogleGenerationProductionPendingApprovalStateOwner.PendingState(
            envelope,
            snapshot,
            "USD",
            2,
            currentEstimateMinorUnits);
    }

    private sealed class RecordingAuthorizationStore : IGoogleGenerationSpendAuthorizationStore
    {
        public int SaveCount { get; private set; }

        public Task SaveApprovedAsync(
            GoogleGenerationSpendAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(authorization);
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task<GoogleGenerationSpendAuthorization?> LoadApprovedAsync(
            GoogleGenerationSubmissionEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(envelope);
            return Task.FromResult<GoogleGenerationSpendAuthorization?>(null);
        }
    }
}
