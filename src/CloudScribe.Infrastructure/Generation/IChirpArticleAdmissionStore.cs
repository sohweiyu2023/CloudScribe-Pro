namespace CloudScribe.Infrastructure.Generation;

/// <summary>Separate read-only installer authority. Runtime callers cannot approve their own policy.</summary>
public interface IChirpArticleAdmissionStore
{
    Task<ChirpArticleAdmissionPolicy?> LoadVerifiedAsync(CancellationToken cancellationToken = default);
}
