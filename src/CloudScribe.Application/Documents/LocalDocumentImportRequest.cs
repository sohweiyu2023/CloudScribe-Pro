namespace CloudScribe.Application.Documents;

public sealed record LocalDocumentImportRequest(
    LocalDocumentImportKind Kind,
    string DisplayName,
    Stream Content,
    long? DeclaredLength = null);
