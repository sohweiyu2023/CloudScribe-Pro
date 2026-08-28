using CloudScribe.Domain.Documents;

namespace CloudScribe.Application.Documents;

public sealed record DocumentSaveRequest(
    Guid DocumentId,
    string Title,
    string Text,
    long ExpectedConcurrencyVersion,
    DocumentRevisionKind RevisionKind,
    string? RevisionName = null,
    string? ImportProvenance = null);
