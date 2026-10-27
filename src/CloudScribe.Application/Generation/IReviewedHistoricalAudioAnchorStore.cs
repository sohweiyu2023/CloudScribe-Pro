namespace CloudScribe.Application.Generation;

/// <summary>Trusted composition boundary for independently admitted acceptance-time evidence.
/// Implementations must not admit caller-selected JSON or compute authority from its own digest.</summary>
public interface IReviewedHistoricalAudioAnchorStore
{
    Task<ReviewedHistoricalAudioAnchor?> LoadAdmittedAsync(string evidenceId, CancellationToken cancellationToken = default);
}
