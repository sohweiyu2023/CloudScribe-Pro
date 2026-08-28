namespace CloudScribe.Application.Generation;

public sealed class GenerationSupportBundleExportCoordinator
{
    private readonly GenerationSupportBundleService _service;
    private readonly Func<GenerationSupportBundle, CancellationToken, Task> _persistMetadataOnlyAsync;
    private int _exportInFlight;

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
        if (Interlocked.CompareExchange(ref _exportInFlight, 1, 0) != 0)
            throw new InvalidOperationException("A generation support-bundle export is already in progress.");

        try
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

            cancellationToken.ThrowIfCancellationRequested();
            await _persistMetadataOnlyAsync(bundle, cancellationToken).ConfigureAwait(false);
            return bundle;
        }
        finally
        {
            Volatile.Write(ref _exportInFlight, 0);
        }
    }
}
