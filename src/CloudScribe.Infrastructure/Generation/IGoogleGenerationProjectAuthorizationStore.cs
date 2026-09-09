namespace CloudScribe.Infrastructure.Generation;

public interface IGoogleGenerationProjectAuthorizationStore
{
    Task<GoogleGenerationProjectAuthorizationEvidence?> LoadCurrentAsync(
        string accountId,
        string projectId,
        string modelId,
        CancellationToken cancellationToken = default);

    Task<GoogleGenerationProjectAuthorizationEvidence?> LoadSingleCurrentForProjectAsync(
        string accountId,
        string projectId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task SaveVerifiedAsync(
        GoogleGenerationProjectAuthorizationEvidence evidence,
        CancellationToken cancellationToken = default);
}
