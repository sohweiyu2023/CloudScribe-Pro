using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Generation;

public interface IVoiceLabAuthorizedAuditionExecutor
{
    Task<GenerationProviderResponse> SubmitAuthorizedAsync(
        VoiceLabAuditionRequest request,
        CancellationToken cancellationToken = default);
}
