using CloudScribe.Application.Batch;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>The complete-article operations a batch uses, one file at a time. The production implementation calls the
/// unchanged <see cref="ChirpArticleService"/>, so every installed admission, budget and dispatch check still applies.</summary>
public interface IBatchArticleWorkflow
{
    Task<ChirpWholeArticleReview> PrepareAsync(Guid documentId, CancellationToken cancellationToken);

    Task<ChirpWholeArticleJob> StartAsync(ChirpWholeArticleConfirmation confirmation, CancellationToken cancellationToken);

    Task<ChirpWholeArticleJob> ReadAsync(Guid jobId, CancellationToken cancellationToken);

    Task<ChirpWholeArticleJob> RunAsync(Guid jobId, NarrationBatch batch, BatchItem item, string destination,
        IProgress<BatchItemProgress>? progress, CancellationToken cancellationToken);
}
