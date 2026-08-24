using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5GenerationSupportBundleExportCoordinatorTests
{
    [Fact]
    public async Task Export_persists_only_policy_admitted_metadata_bundle()
    {
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
            new GenerationSupportBundleMetadata("1.0.0", "windows-x64", "GEN-001", DateTimeOffset.UtcNow));

        Assert.Same(exported, persisted);
        Assert.False(exported.PrivacyDecision.IncludeCacheMedia);
        Assert.False(exported.PrivacyDecision.IncludeCompiledPayload);
        Assert.False(exported.PrivacyDecision.IncludeSourceText);
        Assert.False(exported.PrivacyDecision.IncludePrivateCacheLookupKey);
    }

    [Fact]
    public async Task Export_never_invokes_persistence_when_request_is_not_authorized()
    {
        var persistCalls = 0;
        var coordinator = new GenerationSupportBundleExportCoordinator(
            new GenerationSupportBundleService(),
            (_, _) =>
            {
                persistCalls++;
                return Task.CompletedTask;
            });
        var metadata = new GenerationSupportBundleMetadata("1.0.0", "windows-x64", "GEN-001", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExportAsync(false, true, metadata));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExportAsync(true, false, metadata));
        Assert.Equal(0, persistCalls);
    }

    [Fact]
    public async Task Cancellation_before_export_never_invokes_persistence()
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ExportAsync(true, true, metadata, cancellation.Token));
        Assert.Equal(0, persistCalls);
    }
}
