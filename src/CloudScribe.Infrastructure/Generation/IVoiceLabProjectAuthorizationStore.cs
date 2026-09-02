namespace CloudScribe.Infrastructure.Generation;

public interface IVoiceLabProjectAuthorizationStore
{
    Task<VoiceLabProjectAuthorizationEvidence?> LoadCurrentAsync(
        string providerId,
        string accountId,
        string projectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VoiceLabProjectAuthorizationEvidence>> ListCurrentAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Voice Lab project authorization enumeration is unavailable for this store.");

    Task SaveVerifiedAsync(
        VoiceLabProjectAuthorizationEvidence evidence,
        CancellationToken cancellationToken = default);
}
