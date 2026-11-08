using CloudScribe.Application.Batch;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>Runs one batch file through the unchanged Long Audio service, exactly as the single-document Long Audio window
/// does (same review, continuation check, polling, adaptive splitting, assembly and verification), inside the batch's one
/// generation lock. <paramref name="captureConfirmed"/> is the user's explicit consent to save private provider responses.</summary>
internal sealed class BatchLongAudioWorkflow(GoogleLongAudioDocumentService service, GoogleLongAudioMediaTools tools, bool captureConfirmed)
    : IBatchLongAudioWorkflow
{
    public Task<GoogleLongAudioDocumentReview> ReviewAsync(Guid documentId, CancellationToken cancellationToken) =>
        service.ReviewAsync(documentId, cancellationToken);

    public async Task<GoogleLongAudioContinuationKind> ContinuationAsync(GoogleLongAudioDocumentReview review, CancellationToken cancellationToken) =>
        (await service.InspectContinuationAsync(review, cancellationToken).ConfigureAwait(false)).Kind;

    public Task<GoogleLongAudioDocumentRun> RunAsync(GoogleLongAudioDocumentReview review, IProgress<BatchItemProgress>? progress, Guid itemId,
        CancellationToken cancellationToken)
    {
        var mapped = new Progress<GoogleLongAudioDocumentProgress>(p => progress?.Report(new(itemId, LongAudioDialogPresentation.Headline(p.Stage),
            p.VerifiedScalars ?? 0, p.TotalScalars ?? 0)));
        return service.RunAsync(review, tools, captureConfirmed, new GoogleLongAudioRunControl(), mapped, cancellationToken);
    }

    public async Task<(string Path, string Sha256, long Bytes)?> InspectOutputAsync(GoogleLongAudioDocumentReview review, CancellationToken cancellationToken)
    {
        var article = await service.InspectArticleOutputAsync(review, cancellationToken).ConfigureAwait(false);
        if (article is not null) return (article.Mp3Path, article.Sha256, article.Bytes);
        var single = await service.InspectOutputAsync(review, cancellationToken).ConfigureAwait(false);
        return single is null ? null : (single.LocalPath, single.AudioSha256, single.Bytes);
    }

    public async Task<long?> CommittedUsdCentsAsync(GoogleLongAudioDocumentReview review, CancellationToken cancellationToken) =>
        (await service.InspectRunSummaryAsync(review, cancellationToken).ConfigureAwait(false)).JobRetainedCommitmentsUsdCents;
}
