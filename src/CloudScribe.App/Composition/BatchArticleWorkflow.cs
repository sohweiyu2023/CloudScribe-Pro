using CloudScribe.App.ViewModels;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>Runs one batch file through the unchanged complete-article service, with the same runtime the single
/// "Generate complete article MP3" action uses. Only the document differs; the verified voice is the batch's.</summary>
internal sealed class BatchArticleWorkflow(
    ChirpArticleService articles,
    IDocumentLibrary library,
    Func<ShellViewModel.GoogleTtsStudioRequestSelection, string, Action<ArticleExecutionStage, int>, IChirpWholeArticleRuntime> runtimeFor,
    ShellViewModel.GoogleTtsStudioRequestSelection voice) : IBatchArticleWorkflow
{
    public Task<ChirpWholeArticleReview> PrepareAsync(Guid documentId, CancellationToken cancellationToken) =>
        articles.PrepareWholeArticleAsync(documentId, cancellationToken);

    public Task<ChirpWholeArticleJob> StartAsync(ChirpWholeArticleConfirmation confirmation, CancellationToken cancellationToken) =>
        articles.StartWholeArticleAsync(confirmation, cancellationToken);

    public Task<ChirpWholeArticleJob> ReadAsync(Guid jobId, CancellationToken cancellationToken) =>
        articles.ReadWholeArticleJobAsync(jobId, cancellationToken);

    public async Task<ChirpWholeArticleJob> RunAsync(Guid jobId, NarrationBatch batch, BatchItem item, string destination,
        IProgress<BatchItemProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var document = await library.OpenAsync(item.DocumentId!.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A queued document is missing from the library.");
        if (document.CurrentRevisionId != item.RevisionId) throw new InvalidOperationException("A queued document changed after approval.");
        var selection = ShellViewModel.ForBatchDocument(voice, document.Id, item.RevisionId!.Value, document.Text);
        var runtime = runtimeFor(selection, destination, (stage, ordinal) => progress?.Report(new(item.ItemId, Describe(stage, ordinal), 0, 0)));
        var coverage = new Progress<ChirpArticleProgress>(p => progress?.Report(new(item.ItemId, "Saving verified parts", p.SavedCoverageParts, p.TotalCoverageParts)));
        return await articles.RunWholeArticleAsync(jobId, runtime, coverage, cancellationToken).ConfigureAwait(false);
    }

    internal static string Describe(ArticleExecutionStage stage, int ordinal) => stage switch
    {
        ArticleExecutionStage.Reservation => $"Reserving part {ordinal}",
        ArticleExecutionStage.Provider => $"Waiting for Google on part {ordinal}",
        ArticleExecutionStage.AcceptedOutputSave => $"Saving accepted audio for part {ordinal}",
        ArticleExecutionStage.Assembly => "Joining the MP3",
        ArticleExecutionStage.OutputVerification => "Verifying the MP3",
        _ => $"Checking part {ordinal}",
    };
}
