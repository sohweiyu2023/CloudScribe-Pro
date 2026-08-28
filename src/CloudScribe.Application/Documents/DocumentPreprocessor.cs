namespace CloudScribe.Application.Documents;

public sealed class DocumentPreprocessor
{
    private readonly DocumentPreprocessorEngine _engine = new();

    public DocumentPreprocessingPreview Preview(
        string sourceText,
        DocumentPreprocessingOptions? options = null) =>
        _engine.Preview(sourceText, options);
}
