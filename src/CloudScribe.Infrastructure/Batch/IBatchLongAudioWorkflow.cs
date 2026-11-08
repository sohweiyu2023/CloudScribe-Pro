using CloudScribe.Application.Batch;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>The Long Audio operations a batch uses. The production implementation calls the unchanged
/// <see cref="GoogleLongAudioDocumentService"/>, so every ledger, cap, readiness and polling rule still applies.</summary>
public interface IBatchLongAudioWorkflow
{
    Task<GoogleLongAudioDocumentReview> ReviewAsync(Guid documentId, CancellationToken cancellationToken);

    Task<GoogleLongAudioContinuationKind> ContinuationAsync(GoogleLongAudioDocumentReview review, CancellationToken cancellationToken);

    Task<GoogleLongAudioDocumentRun> RunAsync(GoogleLongAudioDocumentReview review, IProgress<BatchItemProgress>? progress, Guid itemId,
        CancellationToken cancellationToken);

    /// <summary>The verified final MP3 saved for this review, if any (path, SHA-256, bytes).</summary>
    Task<(string Path, string Sha256, long Bytes)?> InspectOutputAsync(GoogleLongAudioDocumentReview review, CancellationToken cancellationToken);

    /// <summary>US cents held in the ledger for this file's root request and any splits; null if unavailable.</summary>
    Task<long?> CommittedUsdCentsAsync(GoogleLongAudioDocumentReview review, CancellationToken cancellationToken);
}
