namespace CloudScribe.Application.Documents;

public sealed record LocalDocumentImportResult(
    string SuggestedTitle,
    string Text,
    string Provenance,
    long SourceBytes,
    IReadOnlyList<string> Warnings);
