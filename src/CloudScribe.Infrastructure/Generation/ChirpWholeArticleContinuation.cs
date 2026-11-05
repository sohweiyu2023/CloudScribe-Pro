namespace CloudScribe.Infrastructure.Generation;

/// <summary>Separately reviewed build repair of one stopped recovery family; grants no new window or recovery allowance.</summary>
public sealed record ChirpWholeArticleContinuation(int Version, Guid JobId, string PreviousJobSha256,
    Guid FailedAuthorizationId, ChirpArticleAdmissionPolicy PreviousPolicy);
