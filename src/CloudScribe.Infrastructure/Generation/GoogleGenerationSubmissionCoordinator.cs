using CloudScribe.Application.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed class GoogleGenerationSubmissionCoordinator
{
    private readonly GoogleAuthorizedGenerationExecutor _executor;

    public GoogleGenerationSubmissionCoordinator(GoogleAuthorizedGenerationExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<GenerationProviderResponse> SubmitAsync(
        GenerationProviderRequest request,
        GoogleGenerationExecutionDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);

        if (!decision.MayQueue)
            throw new InvalidOperationException($"Google generation is not queue-admissible: {decision.Reason}");
        if (!decision.MaySubmit)
            throw new InvalidOperationException($"Google generation cannot submit a billable request: {decision.Reason}");
        if (!string.Equals(decision.Reason, "google-generation-authorized", StringComparison.Ordinal))
            throw new InvalidOperationException("Google generation submission requires the current explicit authorization decision.");

        cancellationToken.ThrowIfCancellationRequested();
        return await _executor.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
