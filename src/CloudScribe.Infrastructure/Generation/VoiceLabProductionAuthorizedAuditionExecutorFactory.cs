using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Providers;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed class VoiceLabProductionAuthorizedAuditionExecutorFactory
{
    private readonly VoiceLabProductionAuditionEvidenceLoader _evidenceLoader;
    private readonly VoiceLabProviderAdapterResolver _providerAdapterResolver;

    public VoiceLabProductionAuthorizedAuditionExecutorFactory(
        IProviderAccountStore accounts,
        IProviderCapabilitySnapshotStore capabilities,
        Func<VoiceLabAuditionRequest, CancellationToken, Task<VoiceLabAuditionAuthorizationEvidence?>> loadCurrentEvidence,
        TimeProvider timeProvider,
        IProviderFactoryRegistry providerFactoryRegistry)
    {
        _evidenceLoader = new VoiceLabProductionAuditionEvidenceLoader(
            accounts ?? throw new ArgumentNullException(nameof(accounts)),
            capabilities ?? throw new ArgumentNullException(nameof(capabilities)),
            loadCurrentEvidence ?? throw new ArgumentNullException(nameof(loadCurrentEvidence)),
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        _providerAdapterResolver = new VoiceLabProviderAdapterResolver(
            providerFactoryRegistry ?? throw new ArgumentNullException(nameof(providerFactoryRegistry)));
    }

    public Task<IVoiceLabAuthorizedAuditionExecutor> CreateAsync(
        VoiceLabAuditionRequest request,
        CancellationToken cancellationToken = default) =>
        CreateAsync(request, FailClosedCurrentSelectionResolverAsync, cancellationToken);

    public async Task<IVoiceLabAuthorizedAuditionExecutor> CreateAsync(
        VoiceLabAuditionRequest request,
        Func<VoiceLabCatalogSelection, CancellationToken, Task<VoiceLabCatalogSelection>> currentSelectionResolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(currentSelectionResolver);
        cancellationToken.ThrowIfCancellationRequested();

        var resolver = new VoiceLabAuditionAuthorizationEvidenceResolver(_evidenceLoader.LoadAsync);
        VoiceLabAuditionAuthorizationEvidence approvedEvidence = await resolver.ResolveAsync(
            request,
            cancellationToken).ConfigureAwait(false);

        return new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approvedEvidence,
            resolver.ResolveAsync,
            currentSelectionResolver,
            _providerAdapterResolver.ResolveAuditionAsync);
    }

    private static Task<VoiceLabCatalogSelection> FailClosedCurrentSelectionResolverAsync(
        VoiceLabCatalogSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Production Voice Lab audition current voice revalidation is not configured.");
    }
}
