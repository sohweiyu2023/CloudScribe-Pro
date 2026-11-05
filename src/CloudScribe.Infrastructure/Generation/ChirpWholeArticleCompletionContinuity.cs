namespace CloudScribe.Infrastructure.Generation;

/// <summary>Explicitly reviewed completion/budget-bound continuation; the old expiry stays immutable history, never a replacement timer.</summary>
public sealed record ChirpWholeArticleCompletionContinuity(int Version, Guid JobId, string PreviousJobSha256,
    ChirpArticleAdmissionPolicy PreviousPolicy);
