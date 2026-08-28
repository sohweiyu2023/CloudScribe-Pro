namespace CloudScribe.Application.Documents;

public sealed record DocumentPreprocessingPreview(string SourceText, string OutputText, IReadOnlyList<DocumentSourceMapSegment> SourceMap, IReadOnlyList<string> Warnings);
