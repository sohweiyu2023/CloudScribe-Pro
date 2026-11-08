namespace CloudScribe.Application.Documents;

/// <summary>Creates local, durable source pins and narration copies, never submission authority.</summary>
public interface INarrationCopyStore
{
    Task<DocumentSnapshot> ImportBatchSourceAsync(
        Guid batchId,
        Guid itemId,
        string rawSha256,
        string title,
        string normalizedText,
        CancellationToken cancellationToken = default);

    Task<NarrationCopyPreparation?> FindAsync(Guid narrationDocumentId, CancellationToken cancellationToken = default);

    Task<NarrationCopyPreparation> PrepareAsync(
        Guid sourceDocumentId,
        Guid sourceRevisionId,
        string sourceTextSha256,
        CancellationToken cancellationToken = default);
}
