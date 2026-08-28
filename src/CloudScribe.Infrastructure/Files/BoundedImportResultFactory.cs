using CloudScribe.Application.Documents;

namespace CloudScribe.Infrastructure.Files;

internal static class BoundedImportResultFactory
{
    public static LocalDocumentImportResult Build(LocalDocumentImportRequest request, byte[] source)
    {
        string title = BuildSuggestedTitle(request.DisplayName);
        return request.Kind switch
        {
            LocalDocumentImportKind.PlainText => BuildTextResult(title, source, request, "plain-text"),
            LocalDocumentImportKind.Markdown => BuildTextResult(title, source, request, "markdown"),
            LocalDocumentImportKind.Clipboard => BuildTextResult(title, source, request, "clipboard"),
            LocalDocumentImportKind.Html => BuildHtmlResult(title, source, request),
            LocalDocumentImportKind.Docx => BuildDocxResult(title, source, request),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unsupported local import kind."),
        };
    }

    private static LocalDocumentImportResult BuildTextResult(
        string title,
        byte[] source,
        LocalDocumentImportRequest request,
        string kind) => new(
            title,
            BoundedTextDecoder.Decode(source),
            BuildProvenance(kind, request.DisplayName),
            source.LongLength,
            []);

    private static LocalDocumentImportResult BuildHtmlResult(
        string title,
        byte[] source,
        LocalDocumentImportRequest request) => new(
            title,
            BoundedHtmlTextExtractor.Extract(
                BoundedTextDecoder.Decode(source),
                BoundedImportRequestValidator.MaxSourceBytes),
            BuildProvenance("html-text-only", request.DisplayName),
            source.LongLength,
            ["HTML was imported as inert text; scripts, styles, markup and active content were discarded."]);

    private static LocalDocumentImportResult BuildDocxResult(
        string title,
        byte[] source,
        LocalDocumentImportRequest request) => new(
            title,
            BoundedDocxTextExtractor.Extract(
                source,
                BoundedImportRequestValidator.MaxSourceBytes),
            BuildProvenance("docx-text-only", request.DisplayName),
            source.LongLength,
            ["DOCX was imported as text only; embedded media, macros, external relationships and formatting were not executed or imported."]);

    private static string BuildSuggestedTitle(string displayName)
    {
        string title = Path.GetFileNameWithoutExtension(displayName.Trim());
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Imported document";
        }

        return title.Length <= 200 ? title : title[..200];
    }

    private static string BuildProvenance(string kind, string displayName) =>
        $"local-import:{kind}:{Path.GetFileName(displayName.Trim())}";
}
