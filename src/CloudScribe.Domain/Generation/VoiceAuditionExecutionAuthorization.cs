namespace CloudScribe.Domain.Generation;

public sealed record VoiceAuditionExecutionAuthorization(
    bool UseCachedMedia,
    bool SubmitProviderRequest,
    string Reason);
