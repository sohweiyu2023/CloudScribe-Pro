using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleGenerationProductionRuntimeRequestSourceTests
{
    [Fact]
    public async Task ResolveAsync_MissingAuthorization_FailsBeforeSnapshotOrEstimateResolution()
    {
        var snapshotReads = 0;
        var estimateReads = 0;
        var source = new GoogleGenerationProductionRuntimeRequestSource(
            _ => Task.FromResult<GoogleGenerationSpendAuthorization?>(null),
            _ =>
            {
                snapshotReads++;
                return Task.FromResult<GoogleGenerationUiExecutionSnapshot?>(null);
            },
            _ =>
            {
                estimateReads++;
                return Task.FromResult<long?>(null);
            });

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ResolveAsync(CancellationToken.None));

        Assert.Contains("current durable spend authorization", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, snapshotReads);
        Assert.Equal(0, estimateReads);
    }

    [Fact]
    public async Task ResolveAsync_MissingSnapshot_FailsBeforeEstimateResolution()
    {
        var estimateReads = 0;
        GoogleGenerationSpendAuthorization authorization = CreateAuthorization();
        var source = new GoogleGenerationProductionRuntimeRequestSource(
            _ => Task.FromResult<GoogleGenerationSpendAuthorization?>(authorization),
            _ => Task.FromResult<GoogleGenerationUiExecutionSnapshot?>(null),
            _ =>
            {
                estimateReads++;
                return Task.FromResult<long?>(125);
            });

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ResolveAsync(CancellationToken.None));

        Assert.Contains("exact current compiled UI execution snapshot", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, estimateReads);
    }

    [Fact]
    public async Task ResolveAsync_MissingEstimate_FailsClosed()
    {
        GoogleGenerationSpendAuthorization authorization = CreateAuthorization();
        var snapshot = new GoogleGenerationUiExecutionSnapshot(
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
        var source = new GoogleGenerationProductionRuntimeRequestSource(
            _ => Task.FromResult<GoogleGenerationSpendAuthorization?>(authorization),
            _ => Task.FromResult<GoogleGenerationUiExecutionSnapshot?>(snapshot),
            _ => Task.FromResult<long?>(null));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ResolveAsync(CancellationToken.None));

        Assert.Contains("current provider-billed estimate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static GoogleGenerationSpendAuthorization CreateAuthorization()
    {
        var envelope = new GoogleGenerationSubmissionEnvelope(
            "google-account",
            "credential-ref",
            "capability-v1",
            "pricing-v1",
            12,
            "en-US-Studio-O",
            "MP3",
            "00",
            1);

        return GoogleGenerationSpendAuthorization.Create(
            envelope,
            "USD",
            2,
            approvedEstimateMinorUnits: 125,
            authorizedMaximumMinorUnits: 150);
    }
}
