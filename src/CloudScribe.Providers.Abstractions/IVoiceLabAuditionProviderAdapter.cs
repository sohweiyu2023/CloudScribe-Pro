namespace CloudScribe.Providers.Abstractions;

/// <summary>
/// Exposes provider-native Voice Lab audition submission through an explicitly resolved adapter.
/// Callers must bind this capability to current authorization evidence before invoking it.
/// </summary>
public interface IVoiceLabAuditionProviderAdapter : IProviderAdapter
{
    Task<GenerationProviderResponse> SubmitVoiceLabAuditionAsync(
        VoiceLabProviderAuditionRequest request,
        CancellationToken cancellationToken = default);
}
