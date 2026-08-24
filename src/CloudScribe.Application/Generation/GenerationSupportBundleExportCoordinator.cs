namespace CloudScribe.Application.Generation;

public sealed class GenerationSupportBundleExportCoordinator
{
    private readonly GenerationSupportBundleService _service;
    private readonly Func<GenerationSupportBundle, CancellationToken, Task> _persistMetadataOnlyAsync;

    public GenerationSupportBundleExportCoordinator(
        GenerationSupportBundleService service,
        Func<GenerationSupportBundle, CancellationToken, Task> persistMetadataOnlyAsync)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _persistMetadataOnlyAsync = persistMetadataOnlyAsync ?? throw new ArgumentNullException(nameof(persistMetadataOnlyAsync));
    }

    public async Task<GenerationSupportBundle> ExportAsync(
        bool userExplicitlyRequestedDiagnosticBundle,
        bool currentPolicyAllowsDiagnostics,
        GenerationSupportBundleMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bundle = _service.CreateMetadataOnly(
            userExplicitlyRequestedDiagnosticBundle,
            currentPolicyAllowsDiagnostics,
            metadata);

        if (bundle.PrivacyDecision.IncludeCacheMedia ||
            bundle.PrivacyDecision.IncludeCompiledPayload ||
            bundle.PrivacyDecision.IncludeSourceText ||
            bundle.PrivacyDecision.IncludePrivateCacheLookupKey)
        {
            throw new InvalidOperationException("Generation support bundle export attempted to persist sensitive generation material.");
        }

        // Re-check cancellation immediately before the persistence boundary so a user
        // cancellation that arrives during metadata construction/privacy validation
        // cannot still create a diagnostic artifact on disk.
        cancellationToken.ThrowIfCancellationRequested();
        await _persistMetadataOnlyAsync(bundle, cancellationToken).ConfigureAwait(false);
        return bundle;
    }
}
