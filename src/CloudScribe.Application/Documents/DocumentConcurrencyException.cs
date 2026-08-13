namespace CloudScribe.Application.Documents;

public sealed class DocumentConcurrencyException : InvalidOperationException
{
    public DocumentConcurrencyException(Guid documentId, long expectedVersion)
        : this(documentId, expectedVersion, innerException: null)
    {
    }

    public DocumentConcurrencyException(Guid documentId, long expectedVersion, Exception? innerException)
        : base($"Document {documentId:N} changed after version {expectedVersion}; reload before saving again.", innerException)
    {
        DocumentId = documentId;
        ExpectedVersion = expectedVersion;
    }

    public Guid DocumentId { get; }

    public long ExpectedVersion { get; }
}
