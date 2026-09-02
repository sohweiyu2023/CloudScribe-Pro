namespace CloudScribe.Infrastructure.Generation;

public interface IVoiceLabProjectAuthorizationStore
{
    Task<VoiceLabProjectAuthorizationEvidence?> LoadCurrentAsync(
        string providerId,
        string accountId,
        string projectId,
        CancellationToken cancellationToken = default);

    Task SaveVerifiedAsync(
        VoiceLabProjectAuthorizationEvidence evidence,
        CancellationToken cancellationToken = default);
}
