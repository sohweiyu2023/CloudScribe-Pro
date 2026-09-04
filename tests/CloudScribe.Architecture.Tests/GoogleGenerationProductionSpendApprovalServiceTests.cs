using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleGenerationProductionSpendApprovalServiceTests
{
    [Fact]
    public async Task ApproveExplicitAsyncWithoutUserConfirmationFailsBeforePersistence()
    {
        var store = new RecordingAuthorizationStore();
        var owner = new GoogleGenerationProductionSubmissionStateOwner(store);
        var service = new GoogleGenerationProductionSpendApprovalService(owner);
        GoogleGenerationProductionSpendApprovalService.ApprovalRequest request = CreateRequest(
            currentEstimateMinorUnits: 125,
            authorizedMaximumMinorUnits: 150,
            confirmedByUser: false);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApproveExplicitAsync(request, CancellationToken.None));

        Assert.Contains("explicit user confirmation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.SaveCount);
        Assert.Null(await owner.ResolveCurrentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ApproveExplicitAsyncEstimateAboveAuthorizedCeilingFailsBeforePersistence()
    {
        var store = new RecordingAuthorizationStore();
        var owner = new GoogleGenerationProductionSubmissionStateOwner(store);
        var service = new GoogleGenerationProductionSpendApprovalService(owner);
        GoogleGenerationProductionSpendApprovalService.ApprovalRequest request = CreateRequest(
            currentEstimateMinorUnits: 151,
            authorizedMaximumMinorUnits: 150,
            confirmedByUser: true);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApproveExplicitAsync(request, CancellationToken.None));

        Assert.Contains("estimate exceeds", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.SaveCount);
        Assert.Null(await owner.ResolveCurrentAsync(CancellationToken.None));
    }

    private static GoogleGenerationProductionSpendApprovalService.ApprovalRequest CreateRequest(
        long currentEstimateMinorUnits,
        long authorizedMaximumMinorUnits,
        bool confirmedByUser)
    {
        return new GoogleGenerationProductionSpendApprovalService.ApprovalRequest(
            CreateEnvelope(),
            CreateIncompleteSnapshot(),
            "USD",
            2,
            currentEstimateMinorUnits,
            authorizedMaximumMinorUnits,
            confirmedByUser);
    }

    private static GoogleGenerationSubmissionEnvelope CreateEnvelope()
    {
        return new GoogleGenerationSubmissionEnvelope(
            "google-account",
            "credential-ref",
            "capability-v1",
            "pricing-v1",
            12,
            "en-US-Studio-O",
            "MP3",
            "00",
            1);
    }

    private static GoogleGenerationUiExecutionSnapshot CreateIncompleteSnapshot()
    {
        return new GoogleGenerationUiExecutionSnapshot(
            null!,
            false,
            false,
            false,
            false,
            null!,
            null!,
            null!,
            null!,
            GoogleGenerationReconciliationResolutionEvidence.None,
            false,
            false,
            false,
            false);
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
