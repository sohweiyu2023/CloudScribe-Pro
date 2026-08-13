namespace CloudScribe.Application.Documents;

public enum LocalDocumentImportKind
{
    PlainText = 0,
    Markdown = 1,
    Html = 2,
    Docx = 3,
    Clipboard = 4,
}

public sealed record LocalDocumentImportRequest(
    LocalDocumentImportKind Kind,
    string DisplayName,
    Stream Content,
    long? DeclaredLength = null);

public sealed record LocalDocumentImportResult(
    string SuggestedTitle,
    string Text,
    string Provenance,
    long SourceBytes,
    IReadOnlyList<string> Warnings);

public interface ILocalDocumentImporter
{
    Task<LocalDocumentImportResult> ImportAsync(
        LocalDocumentImportRequest request,
        CancellationToken cancellationToken = default);
}
