using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleGenerationProductionRuntimeRequestSourceTests
{
    [Fact]
    public async Task ResolveAsyncMissingCurrentSubmissionFailsBeforeDurableAuthorizationRead()
    {
        var authorizationReads = 0;
        var store = new StubAuthorizationStore((_, _) =>
        {
            authorizationReads++;
            return Task.FromResult<GoogleGenerationSpendAuthorization?>(null);
        });
        var source = new GoogleGenerationProductionRuntimeRequestSource(
            store,
            _ => Task.FromResult<GoogleGenerationProductionSubmissionState?>(null));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ResolveAsync(CancellationToken.None));

        Assert.Contains("coherent current compiled submission state", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, authorizationReads);
    }

    [Fact]
    public async Task ResolveAsyncMissingSnapshotFailsBeforeDurableAuthorizationRead()
    {
        var authorizationReads = 0;
        GoogleGenerationSubmissionEnvelope envelope = CreateEnvelope();
        var store = new StubAuthorizationStore((_, _) =>
        {
            authorizationReads++;
            return Task.FromResult<GoogleGenerationSpendAuthorization?>(CreateAuthorization());
        });
        var state = new GoogleGenerationProductionSubmissionState(envelope, null!, 125);
        var source = new GoogleGenerationProductionRuntimeRequestSource(
            store,
            _ => Task.FromResult<GoogleGenerationProductionSubmissionState?>(state));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ResolveAsync(CancellationToken.None));

        Assert.Contains("exact current compiled UI execution snapshot", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, authorizationReads);
    }

    [Fact]
    public async Task ResolveAsyncNegativeEstimateFailsBeforeDurableAuthorizationRead()
    {
        var authorizationReads = 0;
        GoogleGenerationSubmissionEnvelope envelope = CreateEnvelope();
        var store = new StubAuthorizationStore((_, _) =>
        {
            authorizationReads++;
            return Task.FromResult<GoogleGenerationSpendAuthorization?>(CreateAuthorization());
        });
        var state = new GoogleGenerationProductionSubmissionState(
            envelope,
            CreateIncompleteSnapshot(),
            -1);
        var source = new GoogleGenerationProductionRuntimeRequestSource(
            store,
            _ => Task.FromResult<GoogleGenerationProductionSubmissionState?>(state));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ResolveAsync(CancellationToken.None));

        Assert.Contains("current estimate cannot be negative", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, authorizationReads);
    }

    [Fact]
    public async Task ResolveAsyncMissingDurableAuthorizationForExactEnvelopeFailsClosed()
    {
        GoogleGenerationSubmissionEnvelope envelope = CreateEnvelope();
        GoogleGenerationSubmissionEnvelope? observedEnvelope = null;
        var store = new StubAuthorizationStore((candidate, _) =>
        {
            observedEnvelope = candidate;
            return Task.FromResult<GoogleGenerationSpendAuthorization?>(null);
        });
        var state = new GoogleGenerationProductionSubmissionState(
            envelope,
            CreateIncompleteSnapshot(),
            125);
        var source = new GoogleGenerationProductionRuntimeRequestSource(
            store,
            _ => Task.FromResult<GoogleGenerationProductionSubmissionState?>(state));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ResolveAsync(CancellationToken.None));

        Assert.Contains("durable spend authorization for the exact current submission envelope", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(envelope, observedEnvelope);
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

    private static GoogleGenerationSpendAuthorization CreateAuthorization()
    {
        return GoogleGenerationSpendAuthorization.Create(
            CreateEnvelope(),
            "USD",
            2,
            approvedEstimateMinorUnits: 125,
            authorizedMaximumMinorUnits: 150);
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

    private sealed class StubAuthorizationStore : IGoogleGenerationSpendAuthorizationStore
    {
        private readonly Func<GoogleGenerationSubmissionEnvelope, CancellationToken, Task<GoogleGenerationSpendAuthorization?>> _load;

        public StubAuthorizationStore(
            Func<GoogleGenerationSubmissionEnvelope, CancellationToken, Task<GoogleGenerationSpendAuthorization?>> load)
        {
            _load = load;
        }

        public Task SaveApprovedAsync(
            GoogleGenerationSpendAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GoogleGenerationSpendAuthorization?> LoadApprovedAsync(
            GoogleGenerationSubmissionEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            return _load(envelope, cancellationToken);
        }
    }
}
