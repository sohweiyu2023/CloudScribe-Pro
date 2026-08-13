namespace CloudScribe.Application.Documents;

public sealed class DocumentConcurrencyException : InvalidOperationException
{
    public DocumentConcurrencyException(Guid documentId, long expectedVersion)
        : base($"Document {documentId:N} changed after version {expectedVersion}; reload before saving again.")
    {
        DocumentId = documentId;
        ExpectedVersion = expectedVersion;
    }

    public Guid DocumentId { get; }

    public long ExpectedVersion { get; }
}
