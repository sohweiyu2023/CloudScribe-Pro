using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5GenerationSupportBundleExportCoordinatorTests
{
    [Fact]
    public async Task ExportPersistsOnlyPolicyAdmittedMetadataBundle()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        GenerationSupportBundle? persisted = null;
        var coordinator = new GenerationSupportBundleExportCoordinator(
            new GenerationSupportBundleService(),
            (bundle, _) =>
            {
                persisted = bundle;
                return Task.CompletedTask;
            });

        var exported = await coordinator.ExportAsync(
            userExplicitlyRequestedDiagnosticBundle: true,
            currentPolicyAllowsDiagnostics: true,
            new GenerationSupportBundleMetadata("1.0.0", "windows-x64", "GEN-001", DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(true);

        Assert.Same(exported, persisted);
        Assert.False(exported.PrivacyDecision.IncludeCacheMedia);
        Assert.False(exported.PrivacyDecision.IncludeCompiledPayload);
        Assert.False(exported.PrivacyDecision.IncludeSourceText);
        Assert.False(exported.PrivacyDecision.IncludePrivateCacheLookupKey);
    }

    [Fact]
    public async Task ExportNeverInvokesPersistenceWhenRequestIsNotAuthorized()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var persistCalls = 0;
        var coordinator = new GenerationSupportBundleExportCoordinator(
            new GenerationSupportBundleService(),
            (_, _) =>
            {
                persistCalls++;
                return Task.CompletedTask;
            });
        var metadata = new GenerationSupportBundleMetadata("1.0.0", "windows-x64", "GEN-001", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExportAsync(false, true, metadata, cancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExportAsync(true, false, metadata, cancellationToken)).ConfigureAwait(true);
        Assert.Equal(0, persistCalls);
    }

    [Fact]
    public async Task CancellationBeforeExportNeverInvokesPersistence()
    {
        var persistCalls = 0;
        var coordinator = new GenerationSupportBundleExportCoordinator(
            new GenerationSupportBundleService(),
            (_, _) =>
            {
                persistCalls++;
                return Task.CompletedTask;
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var metadata = new GenerationSupportBundleMetadata("1.0.0", "windows-x64", "GEN-001", DateTimeOffset.UtcNow);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ExportAsync(true, true, metadata, cancellation.Token)).ConfigureAwait(true);
        Assert.Equal(0, persistCalls);
    }

    [Fact]
    public async Task ConcurrentExportIsRejectedBeforeSecondPersistence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var persistCalls = 0;
        var persistenceEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePersistence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new GenerationSupportBundleExportCoordinator(
            new GenerationSupportBundleService(),
            async (_, persistCancellationToken) =>
            {
                Interlocked.Increment(ref persistCalls);
                persistenceEntered.TrySetResult();
                await releasePersistence.Task.WaitAsync(persistCancellationToken).ConfigureAwait(false);
            });
        var metadata = new GenerationSupportBundleMetadata("1.0.0", "windows-x64", "GEN-001", DateTimeOffset.UtcNow);

        var first = coordinator.ExportAsync(true, true, metadata, cancellationToken);
        await persistenceEntered.Task.WaitAsync(cancellationToken).ConfigureAwait(true);

        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExportAsync(true, true, metadata, cancellationToken)).ConfigureAwait(true);
        Assert.Contains("already in progress", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Volatile.Read(ref persistCalls));

        releasePersistence.TrySetResult();
        await first.ConfigureAwait(true);
    }
}
