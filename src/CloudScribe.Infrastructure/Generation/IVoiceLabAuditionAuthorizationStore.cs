using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public interface IVoiceLabAuditionAuthorizationStore
{
    Task<VoiceLabAuditionPersistedAuthorization?> LoadCurrentAsync(
        VoiceLabCatalogSelection selection,
        CancellationToken cancellationToken = default);

    Task SaveVerifiedAsync(
        VoiceLabAuditionPersistedAuthorization authorization,
        CancellationToken cancellationToken = default);
}
