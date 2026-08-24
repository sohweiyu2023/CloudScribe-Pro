using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5SupportBundleSingleFlightTests
{
    [Fact]
    public async Task Concurrent_export_is_rejected_before_second_persistence()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistenceCalls = 0;
        var coordinator = new GenerationSupportBundleExportCoordinator(
            new GenerationSupportBundleService(),
            async (_, _) =>
            {
                Interlocked.Increment(ref persistenceCalls);
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
            });
        var metadata = new GenerationSupportBundleMetadata("2.23", "windows-x64", "CACHE-DIAG", DateTimeOffset.UtcNow);

        var first = coordinator.ExportAsync(true, true, metadata);
        await entered.Task.ConfigureAwait(false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExportAsync(true, true, metadata));
        Assert.Equal(1, Volatile.Read(ref persistenceCalls));

        release.TrySetResult();
        await first.ConfigureAwait(false);
        Assert.Equal(1, persistenceCalls);
    }
}
