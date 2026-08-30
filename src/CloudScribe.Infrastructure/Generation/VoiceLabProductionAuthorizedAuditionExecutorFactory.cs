using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed class VoiceLabProductionAuthorizedAuditionExecutorFactory
{
    private readonly VoiceLabProductionAuditionEvidenceLoader _evidenceLoader;
    private readonly Func<VoiceLabAuditionRequest, CancellationToken, Task<GenerationProviderResponse>> _submitAuthorizedTransport;

    public VoiceLabProductionAuthorizedAuditionExecutorFactory(
        IProviderAccountStore accounts,
        IProviderCapabilitySnapshotStore capabilities,
        Func<VoiceLabAuditionRequest, CancellationToken, Task<VoiceLabAuditionAuthorizationEvidence?>> loadCurrentEvidence,
        TimeProvider timeProvider,
        Func<VoiceLabAuditionRequest, CancellationToken, Task<GenerationProviderResponse>> submitAuthorizedTransport)
    {
        _evidenceLoader = new VoiceLabProductionAuditionEvidenceLoader(
            accounts ?? throw new ArgumentNullException(nameof(accounts)),
            capabilities ?? throw new ArgumentNullException(nameof(capabilities)),
            loadCurrentEvidence ?? throw new ArgumentNullException(nameof(loadCurrentEvidence)),
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        _submitAuthorizedTransport = submitAuthorizedTransport ?? throw new ArgumentNullException(nameof(submitAuthorizedTransport));
    }

    public async Task<IVoiceLabAuthorizedAuditionExecutor> CreateAsync(
        VoiceLabAuditionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var resolver = new VoiceLabAuditionAuthorizationEvidenceResolver(_evidenceLoader.LoadAsync);
        VoiceLabAuditionAuthorizationEvidence approvedEvidence = await resolver.ResolveAsync(
            request,
            cancellationToken).ConfigureAwait(false);

        return new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approvedEvidence,
            resolver.ResolveAsync,
            _submitAuthorizedTransport);
    }
}
