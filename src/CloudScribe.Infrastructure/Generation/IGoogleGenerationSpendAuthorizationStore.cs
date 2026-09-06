namespace CloudScribe.Infrastructure.Generation;

public interface IGoogleGenerationSpendAuthorizationStore
{
    Task SaveApprovedAsync(
        GoogleGenerationSpendAuthorization authorization,
        CancellationToken cancellationToken = default);

    Task<GoogleGenerationSpendAuthorization?> LoadApprovedAsync(
        GoogleGenerationSubmissionEnvelope envelope,
        CancellationToken cancellationToken = default);
}
