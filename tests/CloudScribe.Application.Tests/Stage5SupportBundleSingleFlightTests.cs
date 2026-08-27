using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5SupportBundleSingleFlightTests
{
    [Fact]
    public async Task ConcurrentExportIsRejectedBeforeSecondPersistence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistenceCalls = 0;
        var coordinator = new GenerationSupportBundleExportCoordinator(
            new GenerationSupportBundleService(),
            async (_, persistCancellationToken) =>
            {
                Interlocked.Increment(ref persistenceCalls);
                entered.TrySetResult();
                await release.Task.WaitAsync(persistCancellationToken).ConfigureAwait(false);
            });
        var metadata = new GenerationSupportBundleMetadata("2.23", "windows-x64", "CACHE-DIAG", DateTimeOffset.UtcNow);

        var first = coordinator.ExportAsync(true, true, metadata, cancellationToken);
        await entered.Task.WaitAsync(cancellationToken).ConfigureAwait(true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ExportAsync(true, true, metadata, cancellationToken)).ConfigureAwait(true);
        Assert.Equal(1, Volatile.Read(ref persistenceCalls));

        release.TrySetResult();
        await first.ConfigureAwait(true);
        Assert.Equal(1, persistenceCalls);
    }
}
